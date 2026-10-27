"""scene.srec tool (REPLAY_DESIGN 4.2, 4.4, 8.3 REC; encoding notes in the header of src/InsideDev/Runtime/SrecFormat.cs).

Commands (PATH = a .srec file or a folder holding scene.srec or record/scene.srec):
  info PATH [--json]                      header, counts, durations, section sizes, truncation
  verify PATH [--strict] [--size] [--json] CRC, block structure, samples, ids, OBJA/OBJD, string refs, INDX and SEND,
                                          record_meta.csv / objects.tsv rows, size formula of 4.4 (exit 0 ok, 1 problems;
                                          a crash prefix passes unless --strict)
  objects PATH [FILTER] [--kind K]        object table as TSV (id kind path owner comps first last keys ...)
  export PATH ID|virtual|stamps|events [--out CSV]   one object's keys on every channel, the virtual channel, stamps or events
  dump PATH [--out JSON]                  canonical event list (one JSON event per sample; the same list the C# reader produces)
  synth OUT_DIR [--samples N] [--rate R] [--seed S] [--jitter J] [--hitch] [--truth] [--no-sidecars] [--no-shader]
                                          deterministic synthetic recording (every section, a slow rotator and a slow
                                          mover with gaps and pre-keys, a two-level local hierarchy, a cut, OBJA/OBJD,
                                          shader channels of every scope unless --no-shader)
                                          plus objects.tsv, record_meta.csv, events.csv, record.json (and truth.json)
  report PATH [--spec SPEC] [--check Q] [--strict]  record report: every recorded event, the spec events (fired, sample,
                                          time, delta to expect, posted matches) and the content checks of
                                          replay.record.checks ('#subscene' or object path / suffix / name, optional
                                          ' @<from>..<to>' window in seconds or event[+-s]): first active / visible
                                          sample and spans; exit 1 when a check fails
"""
from __future__ import annotations

import argparse
import csv
import fnmatch
import json
import math
import random
import re
import struct
import sys
import zlib
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable, Sequence

MAGIC = 0x43525349
VERSION = 1
TAG_HEAD, TAG_OBJECTS, TAG_SAMPLE, TAG_INDEX, TAG_END = 0x44485253, 0x544A424F, 0x4C504D53, 0x58444E49, 0x444E4553
KIND_NAMES = ("transform", "boyBone", "clothBone", "particles", "light", "material", "probe", "spawned", "rope", "renderer")
K_TRANSFORM, K_BOYBONE, K_CLOTHBONE, K_PARTICLES, K_LIGHT, K_MATERIAL, K_PROBE, K_SPAWNED, K_ROPE, K_RENDERER = range(10)
(S_XF, S_FLAGS, S_PARTICLES, S_LIGHTS, S_MATERIALS, S_VIRTUAL, S_OBJADD, S_OBJDEL, S_PROBES, S_RENDER, S_EVENTS,
 S_MATPROP, S_CAMACTIVE, S_ROPE, S_SHAREDMAT, S_STRINGS, S_SHADERDEF, S_SHADERVAL) = range(1, 19)
SECTION_NAMES = {S_XF: "xf", S_FLAGS: "flags", S_PARTICLES: "particles", S_LIGHTS: "lights", S_MATERIALS: "materials",
                 S_VIRTUAL: "virtual", S_OBJADD: "objAdd", S_OBJDEL: "objDel", S_PROBES: "probes", S_RENDER: "render",
                 S_EVENTS: "events", S_MATPROP: "matProp", S_CAMACTIVE: "camActive", S_ROPE: "rope",
                 S_SHAREDMAT: "sharedMat", S_STRINGS: "strings", S_SHADERDEF: "shaderDef", S_SHADERVAL: "shaderVal"}
FILE_ORDER = (S_STRINGS, S_OBJADD, S_MATPROP, S_SHADERDEF, S_XF, S_FLAGS, S_PARTICLES, S_LIGHTS, S_MATERIALS, S_VIRTUAL,
              S_PROBES, S_RENDER, S_EVENTS, S_CAMACTIVE, S_ROPE, S_SHAREDMAT, S_SHADERVAL, S_OBJDEL)
CONTENT_VERSION = 2
SH_GLOBAL, SH_MATERIAL, SH_BLOCK = 0, 1, 2
SH_SCOPE_NAMES = ("global", "material", "block")
DELTA_SECTIONS = frozenset({S_XF, S_FLAGS, S_LIGHTS, S_PROBES, S_CAMACTIVE, S_SHAREDMAT})
KEY_SECTIONS = frozenset({S_XF, S_FLAGS, S_PARTICLES, S_LIGHTS, S_MATERIALS, S_PROBES, S_CAMACTIVE, S_ROPE, S_SHAREDMAT})
XF_POS, XF_ROT, XF_SCALE, XF_CUT, XF_PARENT, XF_PREKEY = 1, 2, 4, 8, 16, 32
FL_ACTIVE, FL_RENDERER, FL_KINEMATIC, FL_LIGHT = 1, 2, 4, 8
OBJ_CAMDRIVEN, OBJ_SCALEVARIANT, OBJ_EXCLUDEDMATERIAL, OBJ_SPAWNED, OBJ_LOCAL, OBJ_CAMXF, OBJ_SEENACTIVE = 1, 2, 4, 8, 16, 32, 64
SMPL_MARKER, SMPL_HITCH, SMPL_HOLD, SMPL_GAMEFRAME = 1, 2, 4, 8
LI_ENABLED, LI_INTENSITY, LI_COLOR, LI_RANGE, LI_SPOT, LI_COOKIE, LI_STATIC, LI_DIRTY = 1, 2, 4, 8, 16, 32, 64, 128
MAT_FLOAT, MAT_COLOR, MAT_VECTOR, MAT_TEXTURE = 0, 1, 2, 3
MAT_NAMES = ("float", "color", "vector", "texture")
VAL_FLOAT, VAL_VEC4, VAL_INT = 0, 1, 2
VIRT_BOY, VIRT_HUDDLE, VIRT_HUDDLE_MAIN = 1, 2, 4
COMP_NAMES = ("Rigidbody", "Animation", "Animator", "ParticleSystem", "Light", "Renderer", "SkinnedMeshRenderer",
              "MaterialInstance", "D11VelocityBufferTag", "ColorBlendProbe", "LineRenderer", "Actor", "Collider")
FLAG_NAMES = ("active", "renderer", "kinematic", "light")
OBJ_FLAG_NAMES = ("camDriven", "scaleVariant", "excludedMaterial", "spawned", "local", "camXf", "seenActive")
PARTICLE_BYTES = 84
SIZE_XF_BYTES = 41
SIZE_SAMPLE_BYTES = 5 * 1024
HEADER_FIELDS = (("version", "i32"), ("created", "str"), ("spec", "str"), ("level", "str"), ("recorderVersion", "str"),
                 ("clock", "str"), ("captureFramerate", "i32"), ("subscene", "i32"), ("savepoint", "i32"),
                 ("streamVersion", "i32"), ("screenW", "i32"), ("screenH", "i32"), ("fixedDeltaTime", "f64"),
                 ("t0", "f64"), ("t0Real", "f64"), ("stepAtT0", "i64"), ("gameFov", "f32"), ("subscenes", "strs"),
                 ("keptCullers", "strs"), ("forcedCullers", "strs"), ("fogFields", "strs"), ("postFields", "strs"),
                 ("cameraDriven", "strs"), ("posEps", "f32"), ("rotEps", "f32"), ("cut", "f32"), ("vSyncCount", "i32"),
                 ("targetFrameRate", "i32"), ("refreshRate", "i32"))
HEADER_DEFAULTS: dict[str, Any] = {"version": 1, "clock": "real", "subscene": -1, "savepoint": -1}

_U16, _U32, _I32 = struct.Struct("<H"), struct.Struct("<I"), struct.Struct("<i")
_U64, _I64 = struct.Struct("<Q"), struct.Struct("<q")
_F, _D, _3F, _4F = struct.Struct("<f"), struct.Struct("<d"), struct.Struct("<3f"), struct.Struct("<4f")
_STAMP = struct.Struct("<IddqiHH")
_PARTICLE = struct.Struct("<3f3f3f3f3f4BI4f")
_PREAMBLE = struct.Struct("<IHH")
_FRAME = struct.Struct("<II")


class SrecError(Exception):
    pass


def f32(x: float) -> float:
    return _F.unpack(_F.pack(x))[0]


def i32(v: int) -> int:
    v &= 0xFFFFFFFF
    return v - (1 << 32) if v >= 1 << 31 else v


def cf(x: float) -> float | str:
    if math.isnan(x):
        return "NaN"
    if math.isinf(x):
        return "Infinity" if x > 0 else "-Infinity"
    return x


def tag_name(tag: int) -> str:
    return "".join(chr(b) if 32 <= b < 127 else "?" for b in tag.to_bytes(4, "little"))


# ------------------------------------------------------------------ binary codecs

class Cursor:
    __slots__ = ("data", "pos", "end")

    def __init__(self, data: bytes | memoryview, pos: int = 0, end: int | None = None) -> None:
        self.data = data
        self.pos = pos
        self.end = len(data) if end is None else end

    def need(self, n: int) -> None:
        if n < 0 or n > self.end - self.pos:
            raise SrecError(f"payload ends early at byte {self.pos} ({n} more needed)")

    def _s(self, st: struct.Struct) -> tuple:
        self.need(st.size)
        v = st.unpack_from(self.data, self.pos)
        self.pos += st.size
        return v

    def u8(self) -> int:
        self.need(1)
        v = self.data[self.pos]
        self.pos += 1
        return v

    def u16(self) -> int:
        return self._s(_U16)[0]

    def u32(self) -> int:
        return self._s(_U32)[0]

    def i32(self) -> int:
        return self._s(_I32)[0]

    def u64(self) -> int:
        return self._s(_U64)[0]

    def i64(self) -> int:
        return self._s(_I64)[0]

    def f32(self) -> float:
        return self._s(_F)[0]

    def f64(self) -> float:
        return self._s(_D)[0]

    def v3(self) -> tuple[float, float, float]:
        return self._s(_3F)

    def q4(self) -> tuple[float, float, float, float]:
        return self._s(_4F)

    def var(self) -> int:
        v, shift = 0, 0
        while True:
            b = self.u8()
            if shift == 28 and b > 0x0F:
                raise SrecError(f"varint longer than 32 bits at byte {self.pos}")
            v |= (b & 0x7F) << shift
            if b < 0x80:
                return v
            shift += 7

    def int_(self) -> int:
        return i32(self.var())

    def ref(self) -> int:
        return i32(self.var()) - 1

    def count(self, min_bytes: int) -> int:
        v = self.var()
        if min_bytes > 0 and v > (self.end - self.pos) // min_bytes:
            raise SrecError(f"count {v} exceeds the payload at byte {self.pos}")
        return v

    def str_(self) -> str:
        n = self.count(1)
        self.need(n)
        s = bytes(self.data[self.pos:self.pos + n]).decode("utf-8", "replace")
        self.pos += n
        return s

    def ints(self) -> list[int]:
        return [self.int_() for _ in range(self.count(1))]

    def strs(self) -> list[str]:
        return [self.str_() for _ in range(self.count(1))]


class Buf:
    __slots__ = ("b",)

    def __init__(self) -> None:
        self.b = bytearray()

    def u8(self, v: int) -> None:
        self.b.append(v & 0xFF)

    def u16(self, v: int) -> None:
        self.b += _U16.pack(v & 0xFFFF)

    def u32(self, v: int) -> None:
        self.b += _U32.pack(v & 0xFFFFFFFF)

    def i32(self, v: int) -> None:
        self.b += _I32.pack(i32(v))

    def u64(self, v: int) -> None:
        self.b += _U64.pack(v)

    def i64(self, v: int) -> None:
        self.b += _I64.pack(v)

    def f32(self, v: float) -> None:
        self.b += _F.pack(v)

    def f64(self, v: float) -> None:
        self.b += _D.pack(v)

    def v3(self, v: Sequence[float]) -> None:
        self.b += _3F.pack(*v)

    def q4(self, v: Sequence[float]) -> None:
        self.b += _4F.pack(*v)

    def var(self, v: int) -> None:
        v &= 0xFFFFFFFF
        while v >= 0x80:
            self.b.append((v & 0x7F) | 0x80)
            v >>= 7
        self.b.append(v)

    def int_(self, v: int) -> None:
        self.var(v & 0xFFFFFFFF)

    def ref(self, i: int) -> None:
        self.var(0 if i < 0 else i + 1)

    def str_(self, s: str | None) -> None:
        d = (s or "").encode("utf-8")
        self.var(len(d))
        self.b += d

    def ints(self, values: Iterable[int]) -> None:
        values = list(values)
        self.var(len(values))
        for v in values:
            self.int_(v)

    def strs(self, values: Iterable[str]) -> None:
        values = list(values)
        self.var(len(values))
        for v in values:
            self.str_(v)


def write_header(b: Buf, h: dict[str, Any]) -> None:
    for name, kind in HEADER_FIELDS:
        v = h.get(name, HEADER_DEFAULTS.get(name, [] if kind == "strs" else "" if kind == "str" else 0))
        if kind == "str":
            b.str_(v)
        elif kind == "strs":
            b.strs(v)
        else:
            getattr(b, kind)(v)


def read_header(c: Cursor) -> dict[str, Any]:
    h: dict[str, Any] = {}
    for name, kind in HEADER_FIELDS:
        h[name] = c.str_() if kind == "str" else c.strs() if kind == "strs" else getattr(c, kind)()
    return h


def make_object(oid: int, kind: int, path: str, **kw: Any) -> dict[str, Any]:
    o: dict[str, Any] = {"id": oid, "kind": kind, "flags": 0, "instanceId": 0, "parentId": -1, "depth": path.count("/"),
                         "path": path, "owner": "", "ordinals": [], "siblings": [], "comps": 0, "types": [],
                         "pos": (0.0, 0.0, 0.0), "rot": (0.0, 0.0, 0.0, 1.0), "scale": (1.0, 1.0, 1.0), "activeSelf": True,
                         "maxParticles": 0, "simulationSpace": 0, "randomSeed": 0, "lightType": 0, "lightShadows": 0,
                         "floatProps": [], "colorProps": [], "vectorProps": [], "textureProps": [], "ropeVertices": 0,
                         "ropeDrawMode": 0, "ropeLineDivisions": 0, "materialSlots": 0, "extent": 0.0}
    o.update(kw)
    return o


def write_object(b: Buf, o: dict[str, Any], oid: int) -> None:
    b.int_(oid)
    b.u8(o["kind"])
    b.u8(o["flags"])
    b.i32(o["instanceId"])
    b.ref(o["parentId"])
    b.u16(max(0, min(o["depth"], 65535)))
    b.str_(o["path"])
    b.str_(o["owner"])
    b.ints(o["ordinals"])
    b.ints(o["siblings"])
    b.u32(o["comps"])
    b.strs(o["types"])
    b.v3(o["pos"])
    b.q4(o["rot"])
    b.v3(o["scale"])
    b.u8(1 if o["activeSelf"] else 0)
    k = o["kind"]
    if k == K_PARTICLES:
        b.i32(o["maxParticles"])
        b.u8(o["simulationSpace"])
        b.u32(o["randomSeed"])
    elif k == K_LIGHT:
        b.u8(o["lightType"])
        b.u8(o["lightShadows"])
    elif k == K_MATERIAL:
        for key in ("floatProps", "colorProps", "vectorProps", "textureProps"):
            b.var(len(o[key]))
        for key in ("floatProps", "colorProps", "vectorProps", "textureProps"):
            for name in o[key]:
                b.str_(name)
    elif k == K_ROPE:
        b.int_(o["ropeVertices"])
        b.u8(o["ropeDrawMode"])
        b.int_(o["ropeLineDivisions"])
    elif k == K_RENDERER:
        b.int_(o["materialSlots"])
    b.f32(o["extent"])


def read_object(c: Cursor) -> dict[str, Any]:
    o = make_object(c.int_(), c.u8(), "")
    o["flags"] = c.u8()
    o["instanceId"] = c.i32()
    o["parentId"] = c.ref()
    o["depth"] = c.u16()
    o["path"] = c.str_()
    o["owner"] = c.str_()
    o["ordinals"] = c.ints()
    o["siblings"] = c.ints()
    o["comps"] = c.u32()
    o["types"] = c.strs()
    o["pos"] = c.v3()
    o["rot"] = c.q4()
    o["scale"] = c.v3()
    o["activeSelf"] = c.u8() != 0
    k = o["kind"]
    if k == K_PARTICLES:
        o["maxParticles"] = c.i32()
        o["simulationSpace"] = c.u8()
        o["randomSeed"] = c.u32()
    elif k == K_LIGHT:
        o["lightType"] = c.u8()
        o["lightShadows"] = c.u8()
    elif k == K_MATERIAL:
        counts = [c.count(1) for _ in range(4)]
        for key, n in zip(("floatProps", "colorProps", "vectorProps", "textureProps"), counts):
            o[key] = [c.str_() for _ in range(n)]
    elif k == K_ROPE:
        o["ropeVertices"] = c.int_()
        o["ropeDrawMode"] = c.u8()
        o["ropeLineDivisions"] = c.int_()
    elif k == K_RENDERER:
        o["materialSlots"] = c.int_()
    o["extent"] = c.f32()
    o.update({"first": -1, "last": -1, "keys": 0, "addedAt": -1, "deletedAt": -1})
    return o


# ------------------------------------------------------------------ canonical events (the same list the C# reader produces)

def canon_header(h: dict[str, Any]) -> list[Any]:
    out: list[Any] = []
    for name, kind in HEADER_FIELDS:
        v = h.get(name, HEADER_DEFAULTS.get(name, [] if kind == "strs" else "" if kind == "str" else 0))
        if kind == "strs":
            out.append([s or "" for s in v])
        elif kind == "str":
            out.append(v or "")
        elif kind in ("f32", "f64"):
            out.append(cf(f32(v) if kind == "f32" else float(v)))
        else:
            out.append(int(v))
    return out


def canon_object(o: dict[str, Any]) -> list[Any]:
    k = o["kind"]
    ps, li, ma, ro, re = k == K_PARTICLES, k == K_LIGHT, k == K_MATERIAL, k == K_ROPE, k == K_RENDERER
    return [o["id"], k, o["flags"], i32(o["instanceId"]), max(-1, o["parentId"]), max(0, min(o["depth"], 65535)),
            o["path"] or "", o["owner"] or "", [i32(v) for v in o["ordinals"]], [i32(v) for v in o["siblings"]], o["comps"],
            [s or "" for s in o["types"]], *[cf(f32(v)) for v in (*o["pos"], *o["rot"], *o["scale"])],
            1 if o["activeSelf"] else 0,
            o["maxParticles"] if ps else 0, o["simulationSpace"] if ps else 0, o["randomSeed"] if ps else 0,
            o["lightType"] if li else 0, o["lightShadows"] if li else 0,
            list(o["floatProps"]) if ma else [], list(o["colorProps"]) if ma else [],
            list(o["vectorProps"]) if ma else [], list(o["textureProps"]) if ma else [],
            o["ropeVertices"] if ro else 0, o["ropeDrawMode"] if ro else 0, o["ropeLineDivisions"] if ro else 0,
            o["materialSlots"] if re else 0, cf(f32(o["extent"]))]


def canon_floats(values: Iterable[float]) -> list[Any]:
    return [cf(f32(v)) for v in values]


def canon_pose(p: dict[str, Any]) -> list[Any]:
    return canon_floats((*p["p"], *p["r"], p["fov"], *p["aim"], p["near"]))


VIRTUAL_COLUMNS = (["ok", "step"]
                   + [f"r_{n}" for n in ("x", "y", "z", "qx", "qy", "qz", "qw", "fov", "aim_x", "aim_y", "aim_z", "near")]
                   + [f"c_{n}" for n in ("x", "y", "z", "qx", "qy", "qz", "qw", "fov", "aim_x", "aim_y", "aim_z", "near")]
                   + ["cam_x", "cam_y", "cam_z", "aim_x", "aim_y", "aim_z", "up_x", "up_y", "up_z", "vel_x", "vel_y", "vel_z",
                      "aim_distance", "box", "probe_x", "probe_y", "probe_z", "extrap", "hand_x", "hand_y", "hand_z",
                      "hand_qx", "hand_qy", "hand_qz", "hand_qw", "shake_x", "shake_y", "shake_z", "shake_qx", "shake_qy",
                      "shake_qz", "shake_qw", "cut", "pc_x", "pc_y", "pc_z", "pc_qx", "pc_qy", "pc_qz", "pc_qw", "pc_fov",
                      "boy_x", "boy_y", "boy_z", "boy_vx", "boy_vy", "boy_vz", "boy_state", "boy_visual", "huddle_x",
                      "huddle_y", "huddle_z", "huddle_vx", "huddle_vy", "huddle_vz", "huddle_state", "vprobe_x", "vprobe_y",
                      "vprobe_z", "flags"])


def canon_virtual(sample: int, v: dict[str, Any]) -> list[Any]:
    g = v["game"]
    return ["Virtual", sample, 1 if g["ok"] else 0, i32(g["step"]), *canon_pose(g["rendered"]), *canon_pose(g["clean"]),
            *canon_floats((*g["positionCamera"], *g["positionAim"], *g["positionUp"], *g["velocity"], g["aimDistance"])),
            g["box"] or "", *canon_floats((*g["probe"], g["extrapolation"], *g["handOffset"], *g["handRot"],
                                           *g["shakeOffset"], *g["shakeRot"])), 1 if g["cut"] else 0,
            *canon_floats((*v["camPos"], *v["camRot"], v["camFov"], *v["boyPos3"], *v["boyVel3"])),
            v["boyState"] or "", v["boyVisual"] or "", *canon_floats((*v["huddlePos3"], *v["huddleVel3"])),
            v["huddleState"] or "", *canon_floats(v["probe"]), v["flags"]]


def canon_light(sample: int, oid: int, mask: int, v: dict[str, Any]) -> list[Any]:
    en = 1 if mask & LI_ENABLED and v.get("enabled") else 0
    it = v.get("intensity", 0.0) if mask & LI_INTENSITY else 0.0
    col = v.get("color", (0.0, 0.0, 0.0, 0.0)) if mask & LI_COLOR else (0.0, 0.0, 0.0, 0.0)
    rg = v.get("range", 0.0) if mask & LI_RANGE else 0.0
    sp = v.get("spotAngle", 0.0) if mask & LI_SPOT else 0.0
    ck = v.get("cookie", -1) if mask & LI_COOKIE else -1
    ss = 1 if mask & LI_STATIC and v.get("useStaticShadows") else 0
    sd = 1 if mask & LI_DIRTY and v.get("staticShadowsDirty") else 0
    return ["Light", sample, oid, mask, en, *canon_floats((it, *col, rg, sp)), ck if ck >= 0 else -1, ss, sd]


def canon_material(sample: int, oid: int, kind: int, prop: int, value: Sequence[float], texture: int) -> list[Any]:
    v = (0.0, 0.0, 0.0, 0.0)
    t = -1
    if kind == MAT_FLOAT:
        v = (value[0], 0.0, 0.0, 0.0)
    elif kind == MAT_TEXTURE:
        t = texture if texture >= 0 else -1
    else:
        v = tuple(value)
    return ["Material", sample, oid, kind, prop, *canon_floats(v), t]


def canon_shader_val(sample: int, ch: int, typ: int, value: Sequence[float]) -> list[Any]:
    v = (value[0], 0.0, 0.0, 0.0) if typ == VAL_FLOAT else tuple(value)
    return ["ShaderVal", sample, ch, *canon_floats(v)]


def canon_render(sample: int, fld: int, typ: int, value: Sequence[float]) -> list[Any]:
    if typ == VAL_FLOAT:
        v: tuple[float, ...] = (value[0], 0.0, 0.0, 0.0)
    elif typ == VAL_INT:
        v = (float(int(value[0])), 0.0, 0.0, 0.0)
    else:
        v = tuple(value)
    return ["Render", sample, fld, typ, *canon_floats(v)]


# ------------------------------------------------------------------ reader

@dataclass
class Recording:
    path: Path
    size: int = 0
    version: int = 0
    file_flags: int = 0
    fatal: str = ""
    header: dict[str, Any] | None = None
    objects: list[dict[str, Any]] = field(default_factory=list)
    stamps: list[dict[str, Any]] = field(default_factory=list)
    strings: list[str] = field(default_factory=list)
    complete: bool = False
    stop: str = ""
    stop_kind: str = ""
    readable: int = 0
    blocks: list[tuple[str, int, int]] = field(default_factory=list)
    section_entries: Counter = field(default_factory=Counter)
    section_bytes: Counter = field(default_factory=Counter)
    frames: dict[int, list[tuple[int, int, int, int]]] = field(default_factory=dict)
    index: dict[str, Any] | None = None
    send: tuple[int, int] | None = None
    problems: list[str] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)
    events: list[list[Any]] | None = None
    shader: list[dict[str, Any]] = field(default_factory=list)

    @property
    def truncated(self) -> bool:
        return not self.complete

    @property
    def samples(self) -> int:
        return len(self.stamps)

    def problem(self, text: str, limit: int = 50) -> None:
        if len(self.problems) < limit:
            self.problems.append(text)
        elif len(self.problems) == limit:
            self.problems.append("... more problems")

    _data: bytes = field(default=b"", repr=False)

    def particle_bytes(self, oid: int, sample: int) -> bytes | None:
        for s, _entry, data_off, n in self.frames.get(oid, ()):
            if s == sample:
                return self._data[data_off:data_off + n * PARTICLE_BYTES]
        return None


class _Reader:
    def __init__(self, rec: Recording, data: bytes, events: bool) -> None:
        self.rec = rec
        self.data = data
        self.mv = memoryview(data)
        self.ev: list[list[Any]] | None = [] if events else None
        self.header_seen = self.objects_seen = self.begun = False
        self.parents: list[int] = []
        self.renumbered = 0
        self.bad_ref = False

    def emit(self, e: list[Any]) -> None:
        if self.ev is not None:
            self.ev.append(e)

    def run(self) -> None:
        rec, data = self.rec, self.data
        rec.size = len(data)
        if len(data) < 8:
            rec.fatal = "not a scene.srec: shorter than the 8-byte preamble"
            return
        magic, rec.version, rec.file_flags = _PREAMBLE.unpack_from(data, 0)
        if magic != MAGIC:
            rec.fatal = f"not a scene.srec: magic 0x{magic:08X}"
            return
        if rec.version > VERSION:
            rec.fatal = f"scene.srec version {rec.version} is newer than this reader ({VERSION})"
            return
        pos = rec.readable = 8
        stop, kind = "", ""
        while True:
            if rec.complete:
                if pos != len(data):
                    rec.warnings.append(f"{len(data) - pos} bytes after SEND ignored")
                break
            if pos == len(data):
                stop, kind = "file ends without INDX/SEND", "eof"
                break
            if len(data) - pos < 12:
                stop, kind = f"incomplete block header at offset {pos}", "incomplete"
                break
            tag, length = _FRAME.unpack_from(data, pos)
            name = tag_name(tag)
            if length > len(data) - pos - 12:
                stop, kind = (f"incomplete {name} block at offset {pos} ({length} payload bytes, {len(data) - pos - 12} left)",
                              "incomplete")
                break
            crc = _U32.unpack_from(data, pos + 8 + length)[0]
            if zlib.crc32(self.mv[pos + 8:pos + 8 + length]) & 0xFFFFFFFF != crc:
                last = pos + 12 + length == len(data)
                stop = f"CRC mismatch in {name} block at offset {pos}" + (
                    " (last block of the file)" if last else " (data follows: corrupt, not a crash prefix)")
                kind = "crc_last" if last else "corrupt"
                break
            if not self.header_seen and tag != TAG_HEAD:
                stop, kind = f"first block is {name}, not SRHD", "structure"
                break
            try:
                self.block(tag, pos, length)
            except SrecError as e:
                stop, kind = f"{name} block at offset {pos}: {e}", "decode"
                break
            rec.blocks.append((name, pos, 12 + length))
            pos += 12 + length
            rec.readable = pos
        if not self.header_seen:
            rec.fatal = "no readable SRHD header" + (f" ({stop})" if stop else "")
            return
        rec.stop, rec.stop_kind = stop, kind
        if kind in ("corrupt", "decode", "structure"):
            rec.problem(f"readable prefix of {rec.samples} samples, {rec.readable} of {rec.size} bytes ({stop})")
        if rec.complete:
            if rec.send and rec.send[0] != rec.readable:
                rec.problem(f"SEND total {rec.send[0]} bytes, readable {rec.readable}")
            if rec.send and rec.send[1] != rec.samples:
                rec.problem(f"SEND sample count {rec.send[1]}, read {rec.samples}")
            if rec.index is None:
                rec.problem("SEND without INDX")
        if rec.index is not None:
            self.cross_check()
        if not self.begun:
            self.begin()
        self.emit(["End", rec.samples, 0 if rec.complete else 1, list(rec.strings),
                   [[o["first"], o["last"], o["keys"]] for o in rec.objects]])
        rec.events = self.ev

    def block(self, tag: int, pos: int, length: int) -> None:
        c = Cursor(self.mv, pos + 8, pos + 8 + length)
        rec = self.rec
        if tag == TAG_HEAD:
            if self.header_seen:
                rec.warnings.append(f"second SRHD at offset {pos} ignored")
                return
            rec.header = read_header(c)
            self.header_seen = True
        elif tag == TAG_OBJECTS:
            if self.objects_seen or self.begun:
                raise SrecError("OBJT after the first sample or twice")
            n = c.count(8)
            for i in range(n):
                o = read_object(c)
                if o["id"] != i:
                    raise SrecError(f"OBJT record {i} has id {o['id']}")
                rec.objects.append(o)
                self.parents.append(o["parentId"])
            for o in rec.objects:
                if not -1 <= o["parentId"] < n:
                    raise SrecError(f"OBJT record {o['id']} has parent id {o['parentId']}")
            self.objects_seen = True
        elif tag == TAG_SAMPLE:
            if not self.begun:
                self.begin()
            self.sample(c, pos, length)
        elif tag == TAG_INDEX:
            self.read_index(c)
        elif tag == TAG_END:
            rec.send = (c.u64(), c.u32())
            rec.complete = True

    def begin(self) -> None:
        self.begun = True
        rec = self.rec
        self.emit(["Begin", canon_header(rec.header or {}), [canon_object(o) for o in rec.objects]])

    def sample(self, c: Cursor, pos: int, length: int) -> None:
        rec = self.rec
        index = rec.samples
        stamped, game_time, real_time, logic, frame, fixed, flags = c._s(_STAMP)
        if stamped != index:
            if self.renumbered == 0:
                rec.problem(f"sample {index} is stamped {stamped}: dense indices used")
            self.renumbered += 1
        if index > 0:
            p = rec.stamps[-1]
            if game_time < p["gameTime"]:
                rec.problem(f"sample {index}: game time {game_time} before {p['gameTime']}")
            if real_time < p["realTime"]:
                rec.problem(f"sample {index}: real time {real_time} before {p['realTime']}")
            if frame < p["frameCount"]:
                rec.problem(f"sample {index}: frame count {frame} before {p['frameCount']}")
        st = {"sample": index, "gameTime": game_time, "realTime": real_time, "logicStep": logic, "frameCount": frame,
              "fixedSteps": fixed, "flags": flags, "offset": pos, "bytes": 12 + length, "xf": 0, "particles": 0,
              "pbytes": 0}
        rec.stamps.append(st)
        self.emit(["Sample", index, cf(game_time), cf(real_time), logic, frame, fixed, flags])
        sections: list[tuple[int, int, int]] = []
        while c.pos < c.end:
            sid = c.u8()
            sl = c.u32()
            c.need(sl)
            sections.append((sid, c.pos, c.pos + sl))
            c.pos += sl
        ids = [s[0] for s in sections]
        if len(set(ids)) != len(ids):
            rec.problem(f"sample {index}: a section appears twice")
        known = [s for s in ids if s in SECTION_NAMES]
        if known != [s for s in FILE_ORDER if s in known]:
            rec.warnings.append(f"sample {index}: sections not in the writer order") if len(rec.warnings) < 50 else None
        for sid, a, b in sections:
            rec.section_bytes[sid] += b - a + 5
            if sid == S_STRINGS:
                self.strings(Cursor(self.mv, a, b))
        for sid, a, b in sections:
            if sid == S_STRINGS:
                continue
            if sid not in SECTION_NAMES:
                rec.warnings.append(f"sample {index}: unknown section {sid} skipped") if len(rec.warnings) < 50 else None
                continue
            sc = Cursor(self.mv, a, b)
            self.section(sid, sc, index, st)
            if sc.pos != b:
                raise SrecError(f"section {sid} of sample {index}: {b - sc.pos} bytes left over")

    def strings(self, c: Cursor) -> None:
        rec = self.rec
        n = c.count(2)
        rec.section_entries[S_STRINGS] += n
        for _ in range(n):
            sid = c.int_()
            s = c.str_()
            if sid < len(rec.strings):
                if rec.strings[sid] != s:
                    rec.problem(f"string {sid} redefined")
                continue
            if sid > len(rec.strings):
                rec.problem(f"string table gap before id {sid}")
            while len(rec.strings) < sid:
                rec.strings.append("")
            rec.strings.append(s)

    def oid(self, v: int, sample: int, sid: int) -> int:
        rec = self.rec
        if not 0 <= v < len(rec.objects):
            raise SrecError(f"object id {v} out of range ({len(rec.objects)} objects)")
        o = rec.objects[v]
        if 0 <= o["deletedAt"] < sample and sid != S_OBJDEL:
            rec.problem(f"sample {sample}: {SECTION_NAMES[sid]} entry for object {v} deleted at sample {o['deletedAt']}")
        return v

    def touch(self, oid: int, sample: int) -> None:
        o = self.rec.objects[oid]
        if o["keys"] == 0:
            o["first"] = sample
        o["last"] = sample
        o["keys"] += 1

    def sref(self, sid: int) -> int:
        if sid >= len(self.rec.strings):
            self.ref_problem(sid)
        return sid

    def name(self, sid: int) -> str:
        if sid < 0:
            return ""
        if sid >= len(self.rec.strings):
            self.ref_problem(sid)
            return ""
        return self.rec.strings[sid]

    def ref_problem(self, sid: int) -> None:
        if not self.bad_ref:
            self.bad_ref = True
            self.rec.problem(f"string id {sid} used before its definition ({len(self.rec.strings)} strings known)")

    def section(self, sid: int, c: Cursor, s: int, st: dict[str, Any]) -> None:
        rec = self.rec
        if sid == S_VIRTUAL:
            rec.section_entries[sid] += 1
            self.emit(self.virtual(c, s))
            return
        n = c.count(1)
        rec.section_entries[sid] += n
        prev = 0
        emit = self.ev is not None
        for _ in range(n):
            if sid == S_SHADERDEF:
                self.shader_def(c, s)
                continue
            if sid == S_SHADERVAL:
                prev += c.int_()
                self.shader_val(c, s, prev)
                continue
            if sid in DELTA_SECTIONS:
                prev += c.int_()
                oid = self.oid(prev, s, sid)
            elif sid in (S_PARTICLES, S_MATERIALS, S_MATPROP, S_ROPE, S_OBJDEL):
                entry = c.pos
                oid = self.oid(c.int_(), s, sid)
            else:
                oid = -1
            if sid in KEY_SECTIONS:
                self.touch(oid, s)
            if sid == S_XF:
                bits = c.u8()
                p = c.v3() if bits & XF_POS else (0.0, 0.0, 0.0)
                q = c.q4() if bits & XF_ROT else (0.0, 0.0, 0.0, 1.0)
                sc = c.v3() if bits & XF_SCALE else (1.0, 1.0, 1.0)
                if bits & XF_PARENT:
                    parent = c.ref()
                    if not -1 <= parent < len(rec.objects):
                        raise SrecError(f"Xf parent id {parent} of id {oid} out of range")
                    self.parents[oid] = parent
                st["xf"] += 1
                if emit:
                    self.emit(["Xf", s, oid, bits, *(cf(v) for v in (*p, *q, *sc)), self.parents[oid]])
            elif sid == S_FLAGS:
                bits = c.u8()
                if emit:
                    self.emit(["Flags", s, oid, bits])
            elif sid == S_PARTICLES:
                count = c.count(0)
                if c.u8() != 0:
                    raise SrecError(f"particle encoding of id {oid} is not raw84")
                size = count * PARTICLE_BYTES
                c.need(size)
                data_off = c.pos
                c.pos += size
                rec.frames.setdefault(oid, []).append((s, entry, data_off, count))
                st["particles"] += count
                st["pbytes"] += size
                if emit:
                    crc = zlib.crc32(self.mv[data_off:data_off + size]) & 0xFFFFFFFF if count else 0
                    self.emit(["Particles", s, oid, count, crc])
            elif sid == S_LIGHTS:
                mask = c.u8()
                v: dict[str, Any] = {}
                if mask & LI_ENABLED:
                    v["enabled"] = c.u8() != 0
                if mask & LI_INTENSITY:
                    v["intensity"] = c.f32()
                if mask & LI_COLOR:
                    v["color"] = c.q4()
                if mask & LI_RANGE:
                    v["range"] = c.f32()
                if mask & LI_SPOT:
                    v["spotAngle"] = c.f32()
                if mask & LI_COOKIE:
                    v["cookie"] = self.sref(c.ref())
                if mask & LI_STATIC:
                    v["useStaticShadows"] = c.u8() != 0
                if mask & LI_DIRTY:
                    v["staticShadowsDirty"] = c.u8() != 0
                if emit:
                    self.emit(canon_light(s, oid, mask, v))
            elif sid == S_MATERIALS:
                kind = c.u8()
                prop = c.int_()
                value: tuple[float, ...] = (0.0, 0.0, 0.0, 0.0)
                texture = -1
                if kind == MAT_FLOAT:
                    value = (c.f32(), 0.0, 0.0, 0.0)
                elif kind in (MAT_COLOR, MAT_VECTOR):
                    value = c.q4()
                elif kind == MAT_TEXTURE:
                    texture = self.sref(c.ref())
                else:
                    raise SrecError(f"material kind {kind} of id {oid}")
                if emit:
                    self.emit(canon_material(s, oid, kind, prop, value, texture))
            elif sid == S_MATPROP:
                kind = c.u8()
                name = c.str_()
                if emit:
                    self.emit(["MatProp", s, oid, kind, name])
            elif sid == S_OBJADD:
                o = read_object(c)
                if o["id"] != len(rec.objects):
                    raise SrecError(f"OBJA id {o['id']}, expected {len(rec.objects)}")
                if not -1 <= o["parentId"] < o["id"]:
                    raise SrecError(f"OBJA id {o['id']} has parent id {o['parentId']}")
                o["addedAt"] = s
                rec.objects.append(o)
                self.parents.append(o["parentId"])
                if emit:
                    self.emit(["ObjAdd", s, canon_object(o)])
            elif sid == S_OBJDEL:
                o = rec.objects[oid]
                if o["deletedAt"] >= 0:
                    rec.problem(f"sample {s}: object {oid} deleted again (first at {o['deletedAt']})")
                else:
                    o["deletedAt"] = s
                if emit:
                    self.emit(["ObjDel", s, oid])
            elif sid == S_PROBES:
                p = c.v3()
                if emit:
                    self.emit(["Probe", s, oid, *(cf(v) for v in p)])
            elif sid == S_RENDER:
                fld = c.int_()
                typ = c.u8()
                if typ == VAL_FLOAT:
                    value = (c.f32(), 0.0, 0.0, 0.0)
                elif typ == VAL_VEC4:
                    value = c.q4()
                elif typ == VAL_INT:
                    value = (f32(float(c.i32())), 0.0, 0.0, 0.0)
                else:
                    raise SrecError(f"render value type {typ}")
                if emit:
                    self.emit(["Render", s, fld, typ, *(cf(v) for v in value)])
            elif sid == S_EVENTS:
                kind_s, name, detail = c.str_(), c.str_(), c.str_()
                if emit:
                    self.emit(["Event", s, kind_s, name, detail])
            elif sid == S_CAMACTIVE:
                active = c.u8() != 0
                if emit:
                    self.emit(["CamActive", s, oid, 1 if active else 0])
            elif sid == S_ROPE:
                count = c.count(12)
                verts = [c.v3() for _ in range(count)]
                if emit:
                    self.emit(["Rope", s, oid, count, [cf(v) for p in verts for v in p]])
            elif sid == S_SHAREDMAT:
                slot = c.int_()
                material = self.sref(c.ref())
                if emit:
                    self.emit(["SharedMat", s, oid, slot, material])

    def shader_def(self, c: Cursor, s: int) -> None:
        rec = self.rec
        ch, scope, target, typ, name = c.int_(), c.u8(), c.ref(), c.u8(), c.str_()
        if ch != len(rec.shader):
            raise SrecError(f"shader channel {ch} defined out of order ({len(rec.shader)} defined)")
        if scope > SH_BLOCK or typ not in (VAL_FLOAT, VAL_VEC4):
            raise SrecError(f"shader channel {ch}: scope {scope} type {typ}")
        if scope == SH_BLOCK:
            target = self.oid(target, s, S_SHADERDEF)
        elif scope == SH_MATERIAL:
            target = self.sref(target)
        else:
            target = -1
        rec.shader.append({"id": ch, "scope": scope, "target": target, "type": typ, "name": name, "definedAt": s,
                           "first": -1, "last": -1, "keys": 0})
        self.emit(["ShaderDef", s, ch, scope, target, typ, name])

    def shader_val(self, c: Cursor, s: int, ch: int) -> None:
        rec = self.rec
        if not 0 <= ch < len(rec.shader):
            raise SrecError(f"shader value of undefined channel {ch} ({len(rec.shader)} defined)")
        d = rec.shader[ch]
        value = (c.f32(), 0.0, 0.0, 0.0) if d["type"] == VAL_FLOAT else c.q4()
        if d["keys"] == 0:
            d["first"] = s
        d["last"] = s
        d["keys"] += 1
        self.emit(canon_shader_val(s, ch, d["type"], value))

    def virtual(self, c: Cursor, s: int) -> list[Any]:
        def pose() -> list[Any]:
            p, r, fov, aim, near = c.v3(), c.q4(), c.f32(), c.v3(), c.f32()
            return [cf(v) for v in (*p, *r, fov, *aim, near)]

        ok, step = c.u8() != 0, c.i32()
        out: list[Any] = ["Virtual", s, 1 if ok else 0, step, *pose(), *pose()]
        out += [cf(v) for v in (*c.v3(), *c.v3(), *c.v3(), *c.v3(), c.f32())]
        out.append(self.name(c.ref()))
        out += [cf(v) for v in (*c.v3(), c.f32(), *c.v3(), *c.q4(), *c.v3(), *c.q4())]
        out.append(1 if c.u8() else 0)
        out += [cf(v) for v in (*c.v3(), *c.q4(), c.f32(), *c.v3(), *c.v3())]
        out += [self.name(c.ref()), self.name(c.ref())]
        out += [cf(v) for v in (*c.v3(), *c.v3())]
        out.append(self.name(c.ref()))
        out += [cf(v) for v in c.v3()]
        out.append(c.u8())
        return out

    def read_index(self, c: Cursor) -> None:
        ns = c.count(8)
        offsets = [c.u64() for _ in range(ns)]
        no = c.count(3)
        stats = [(c.int_(), c.int_(), c.int_()) for _ in range(no)]
        frames: dict[int, list[tuple[int, int]]] = {}
        for _ in range(c.count(2)):
            oid = c.int_()
            frames[oid] = [(c.int_(), c.u64()) for _ in range(c.count(9))]
        strings = c.strs()
        self.rec.index = {"offsets": offsets, "stats": stats, "frames": frames, "strings": strings}

    def cross_check(self) -> None:
        rec, ix = self.rec, self.rec.index
        assert ix is not None
        if len(ix["offsets"]) != rec.samples:
            rec.problem(f"INDX: {len(ix['offsets'])} samples, read {rec.samples}")
        elif any(a != st["offset"] for a, st in zip(ix["offsets"], rec.stamps)):
            rec.problem("INDX: sample block offsets differ from the blocks")
        if len(ix["stats"]) != len(rec.objects):
            rec.problem(f"INDX: {len(ix['stats'])} objects, read {len(rec.objects)}")
        else:
            for i, (o, (first, last, keys)) in enumerate(zip(rec.objects, ix["stats"])):
                if o["keys"] != keys or (keys and (o["first"], o["last"]) != (first, last)):
                    rec.problem(f"INDX: key statistics of object {i} ({first}, {last}, {keys}) vs read "
                                f"({o['first']}, {o['last']}, {o['keys']})")
                    break
        scanned = {oid: [(f[0], f[1]) for f in fl] for oid, fl in rec.frames.items()}
        if scanned != ix["frames"]:
            rec.problem(f"INDX: particle frames differ ({len(ix['frames'])} systems indexed, {len(scanned)} read)")
        common = min(len(ix["strings"]), len(rec.strings))
        if ix["strings"][:common] != rec.strings[:common]:
            rec.problem("INDX: string table differs from the inline strings")
        if len(ix["strings"]) < len(rec.strings):
            rec.problem(f"INDX: {len(ix['strings'])} strings, {len(rec.strings)} inline")
        rec.strings.extend(ix["strings"][len(rec.strings):])


def resolve(path: Path) -> Path:
    if path.is_dir():
        for cand in (path / "scene.srec", path / "record" / "scene.srec"):
            if cand.is_file():
                return cand
        raise FileNotFoundError(f"no scene.srec in {path}")
    return path


def read_srec(path: Path, events: bool = False) -> Recording:
    path = resolve(path)
    data = path.read_bytes()
    rec = Recording(path=path)
    rec._data = data
    _Reader(rec, data, events).run()
    return rec


# ------------------------------------------------------------------ writer (synth, tests)

class Writer:
    """Python twin of SrecWriter (no thread). Records the canonical events a reader must return in `expected`."""

    def __init__(self, path: Path, header: dict[str, Any]) -> None:
        self.f = path.open("wb")
        self.header = header
        self.f.write(_PREAMBLE.pack(MAGIC, VERSION, 0))
        self.pos = 8
        self.strings: list[str] = []
        self.string_ids: dict[str, int] = {}
        self.sent = 0
        self.objects: list[dict[str, Any]] = []
        self.parents: list[int] = []
        self.deleted: set[int] = set()
        self.keys: list[list[int]] = []
        self.touches: list[tuple[int, int]] = []
        self.offsets: list[int] = []
        self.frames: list[tuple[int, int, int]] = []
        self.expected: list[list[Any]] = []
        self.sample_starts: list[int] = []
        self.strings_at: list[int] = []
        self.sample_stats: list[dict[str, Any]] = []
        self.samples = 0
        self.cur = -1
        self.sec: dict[int, Buf] = {}
        self.cnt: Counter = Counter()
        self.prev: Counter = Counter()
        self.pend: dict[int, list[list[Any]]] = {}
        self.stamp: tuple = ()
        self.pframes: list[tuple[int, int]] = []
        self.stat: dict[str, Any] = {}
        self.shader_types: list[int] = []
        b = Buf()
        write_header(b, header)
        self._block(TAG_HEAD, b.b)

    def _block(self, tag: int, payload: bytes | bytearray) -> int:
        start = self.pos
        self.f.write(_FRAME.pack(tag, len(payload)))
        self.f.write(payload)
        self.f.write(_U32.pack(zlib.crc32(payload) & 0xFFFFFFFF))
        self.pos += 12 + len(payload)
        return start

    def string_id(self, s: str | None) -> int:
        if not s:
            return -1
        if s not in self.string_ids:
            self.string_ids[s] = len(self.strings)
            self.strings.append(s)
        return self.string_ids[s]

    def set_objects(self, table: list[dict[str, Any]]) -> None:
        b = Buf()
        b.var(len(table))
        for i, o in enumerate(table):
            assert o["id"] == i
            write_object(b, o, i)
        self.objects = [dict(o) for o in table]
        self.parents = [o["parentId"] for o in table]
        self.keys = [[-1, -1, 0] for _ in table]
        self._block(TAG_OBJECTS, b.b)
        self.expected.append(["Begin", canon_header(self.header), [canon_object(o) for o in self.objects]])

    def begin_sample(self, game_time: float, real_time: float, logic_step: int, frame: int, fixed: int, flags: int) -> None:
        self.cur = self.samples
        self.stamp = (self.cur, game_time, real_time, logic_step, frame, fixed, flags)
        self.sec = {s: Buf() for s in FILE_ORDER}
        self.cnt = Counter()
        self.prev = Counter()
        self.pend = {s: [] for s in FILE_ORDER}
        self.pframes = []
        self.stat = {"xf": 0, "particles": 0, "pbytes": 0}
        self.sample_starts.append(len(self.expected))
        self.expected.append(["Sample", self.cur, cf(game_time), cf(real_time), logic_step, frame, fixed, flags])

    def _id(self, sid: int, oid: int, delta: bool) -> None:
        assert 0 <= oid < len(self.objects), oid
        if delta:
            assert oid >= self.prev[sid], (sid, oid)
            self.sec[sid].var(oid - self.prev[sid])
            self.prev[sid] = oid
        else:
            self.sec[sid].int_(oid)
        self.cnt[sid] += 1

    def _touch(self, oid: int) -> None:
        self.touches.append((self.cur, oid))
        k = self.keys[oid]
        if k[2] == 0:
            k[0] = self.cur
        k[1] = self.cur
        k[2] += 1

    def xf(self, oid: int, bits: int, pos: Sequence[float] = (0, 0, 0), rot: Sequence[float] = (0, 0, 0, 1),
           scale: Sequence[float] = (1, 1, 1), parent: int = -1) -> None:
        self._id(S_XF, oid, True)
        b = self.sec[S_XF]
        b.u8(bits)
        if bits & XF_POS:
            b.v3(pos)
        if bits & XF_ROT:
            b.q4(rot)
        if bits & XF_SCALE:
            b.v3(scale)
        if bits & XF_PARENT:
            assert -1 <= parent < len(self.objects)
            b.ref(parent)
            self.parents[oid] = parent
        p = pos if bits & XF_POS else (0.0, 0.0, 0.0)
        q = rot if bits & XF_ROT else (0.0, 0.0, 0.0, 1.0)
        s = scale if bits & XF_SCALE else (1.0, 1.0, 1.0)
        self.pend[S_XF].append(["Xf", self.cur, oid, bits, *canon_floats((*p, *q, *s)), self.parents[oid]])
        self.stat["xf"] += 1
        self._touch(oid)

    def flags(self, oid: int, bits: int) -> None:
        self._id(S_FLAGS, oid, True)
        self.sec[S_FLAGS].u8(bits)
        self.pend[S_FLAGS].append(["Flags", self.cur, oid, bits])
        self._touch(oid)

    def particles(self, oid: int, raw: bytes) -> None:
        n = len(raw) // PARTICLE_BYTES
        b = self.sec[S_PARTICLES]
        self.pframes.append((oid, len(b.b)))
        self._id(S_PARTICLES, oid, False)
        b.var(n)
        b.u8(0)
        b.b += raw[:n * PARTICLE_BYTES]
        self.pend[S_PARTICLES].append(["Particles", self.cur, oid, n, zlib.crc32(raw[:n * PARTICLE_BYTES]) & 0xFFFFFFFF if n else 0])
        self.stat["particles"] += n
        self.stat["pbytes"] += n * PARTICLE_BYTES
        self._touch(oid)

    def light(self, oid: int, mask: int, v: dict[str, Any]) -> None:
        self._id(S_LIGHTS, oid, True)
        b = self.sec[S_LIGHTS]
        b.u8(mask)
        if mask & LI_ENABLED:
            b.u8(1 if v.get("enabled") else 0)
        if mask & LI_INTENSITY:
            b.f32(v.get("intensity", 0.0))
        if mask & LI_COLOR:
            b.q4(v.get("color", (0.0, 0.0, 0.0, 0.0)))
        if mask & LI_RANGE:
            b.f32(v.get("range", 0.0))
        if mask & LI_SPOT:
            b.f32(v.get("spotAngle", 0.0))
        if mask & LI_COOKIE:
            b.ref(v.get("cookie", -1))
        if mask & LI_STATIC:
            b.u8(1 if v.get("useStaticShadows") else 0)
        if mask & LI_DIRTY:
            b.u8(1 if v.get("staticShadowsDirty") else 0)
        self.pend[S_LIGHTS].append(canon_light(self.cur, oid, mask, v))
        self._touch(oid)

    def material(self, oid: int, kind: int, prop: int, value: Sequence[float] = (0, 0, 0, 0), texture: int = -1) -> None:
        self._id(S_MATERIALS, oid, False)
        b = self.sec[S_MATERIALS]
        b.u8(kind)
        b.int_(prop)
        if kind == MAT_FLOAT:
            b.f32(value[0])
        elif kind == MAT_TEXTURE:
            b.ref(texture)
        else:
            b.q4(value)
        self.pend[S_MATERIALS].append(canon_material(self.cur, oid, kind, prop, value, texture))
        self._touch(oid)

    def mat_prop(self, oid: int, kind: int, name: str) -> None:
        self._id(S_MATPROP, oid, False)
        self.sec[S_MATPROP].u8(kind)
        self.sec[S_MATPROP].str_(name)
        self.pend[S_MATPROP].append(["MatProp", self.cur, oid, kind, name or ""])

    def virtual(self, v: dict[str, Any]) -> None:
        g = v["game"]
        b = self.sec[S_VIRTUAL]
        b.u8(1 if g["ok"] else 0)
        b.i32(g["step"])
        for pose in (g["rendered"], g["clean"]):
            b.v3(pose["p"])
            b.q4(pose["r"])
            b.f32(pose["fov"])
            b.v3(pose["aim"])
            b.f32(pose["near"])
        for key in ("positionCamera", "positionAim", "positionUp", "velocity"):
            b.v3(g[key])
        b.f32(g["aimDistance"])
        b.ref(self.string_id(g["box"]))
        b.v3(g["probe"])
        b.f32(g["extrapolation"])
        b.v3(g["handOffset"])
        b.q4(g["handRot"])
        b.v3(g["shakeOffset"])
        b.q4(g["shakeRot"])
        b.u8(1 if g["cut"] else 0)
        b.v3(v["camPos"])
        b.q4(v["camRot"])
        b.f32(v["camFov"])
        b.v3(v["boyPos3"])
        b.v3(v["boyVel3"])
        b.ref(self.string_id(v["boyState"]))
        b.ref(self.string_id(v["boyVisual"]))
        b.v3(v["huddlePos3"])
        b.v3(v["huddleVel3"])
        b.ref(self.string_id(v["huddleState"]))
        b.v3(v["probe"])
        b.u8(v["flags"])
        self.cnt[S_VIRTUAL] = 1
        self.pend[S_VIRTUAL].append(canon_virtual(self.cur, v))

    def obj_add(self, o: dict[str, Any]) -> None:
        assert o["id"] == len(self.objects) and -1 <= o["parentId"] < o["id"]
        write_object(self.sec[S_OBJADD], o, o["id"])
        self.cnt[S_OBJADD] += 1
        self.objects.append(dict(o, addedAt=self.cur))
        self.parents.append(o["parentId"])
        self.keys.append([-1, -1, 0])
        self.pend[S_OBJADD].append(["ObjAdd", self.cur, canon_object(o)])

    def obj_del(self, oid: int) -> None:
        self._id(S_OBJDEL, oid, False)
        self.deleted.add(oid)
        self.pend[S_OBJDEL].append(["ObjDel", self.cur, oid])

    def probe(self, oid: int, pos: Sequence[float]) -> None:
        self._id(S_PROBES, oid, True)
        self.sec[S_PROBES].v3(pos)
        self.pend[S_PROBES].append(["Probe", self.cur, oid, *canon_floats(pos)])
        self._touch(oid)

    def render(self, fld: int, typ: int, value: Sequence[float]) -> None:
        b = self.sec[S_RENDER]
        b.int_(fld)
        b.u8(typ)
        if typ == VAL_FLOAT:
            b.f32(value[0])
        elif typ == VAL_INT:
            b.i32(int(value[0]))
        else:
            b.q4(value)
        self.cnt[S_RENDER] += 1
        self.pend[S_RENDER].append(canon_render(self.cur, fld, typ, value))

    def event(self, kind: str, name: str, detail: str) -> None:
        b = self.sec[S_EVENTS]
        b.str_(kind)
        b.str_(name)
        b.str_(detail)
        self.cnt[S_EVENTS] += 1
        self.pend[S_EVENTS].append(["Event", self.cur, kind or "", name or "", detail or ""])

    def cam_active(self, oid: int, active: bool) -> None:
        self._id(S_CAMACTIVE, oid, True)
        self.sec[S_CAMACTIVE].u8(1 if active else 0)
        self.pend[S_CAMACTIVE].append(["CamActive", self.cur, oid, 1 if active else 0])
        self._touch(oid)

    def rope(self, oid: int, verts: Sequence[Sequence[float]]) -> None:
        self._id(S_ROPE, oid, False)
        b = self.sec[S_ROPE]
        b.var(len(verts))
        for v in verts:
            b.v3(v)
        self.pend[S_ROPE].append(["Rope", self.cur, oid, len(verts), canon_floats([x for v in verts for x in v])])
        self._touch(oid)

    def shared_mat(self, oid: int, slot: int, material: int) -> None:
        self._id(S_SHAREDMAT, oid, True)
        self.sec[S_SHAREDMAT].int_(slot)
        self.sec[S_SHAREDMAT].ref(material)
        self.pend[S_SHAREDMAT].append(["SharedMat", self.cur, oid, slot, material if material >= 0 else -1])
        self._touch(oid)

    def shader_def(self, scope: int, target: int, typ: int, name: str) -> int:
        assert scope <= SH_BLOCK and typ in (VAL_FLOAT, VAL_VEC4) and name, (scope, typ, name)
        if scope == SH_GLOBAL:
            target = -1
        elif scope == SH_MATERIAL:
            assert 0 <= target < len(self.strings), target
        else:
            assert 0 <= target < len(self.objects), target
        ch = len(self.shader_types)
        self.shader_types.append(typ)
        b = self.sec[S_SHADERDEF]
        b.int_(ch)
        b.u8(scope)
        b.ref(target)
        b.u8(typ)
        b.str_(name)
        self.cnt[S_SHADERDEF] += 1
        self.pend[S_SHADERDEF].append(["ShaderDef", self.cur, ch, scope, target, typ, name])
        return ch

    def shader_val(self, ch: int, value: Sequence[float]) -> None:
        assert 0 <= ch < len(self.shader_types) and ch >= self.prev[S_SHADERVAL], ch
        b = self.sec[S_SHADERVAL]
        b.var(ch - self.prev[S_SHADERVAL])
        self.prev[S_SHADERVAL] = ch
        typ = self.shader_types[ch]
        if typ == VAL_FLOAT:
            b.f32(value[0])
        else:
            b.q4(value)
        self.cnt[S_SHADERVAL] += 1
        self.pend[S_SHADERVAL].append(canon_shader_val(self.cur, ch, typ, value))

    def end_sample(self) -> None:
        st = self.sec[S_STRINGS]
        for i in range(self.sent, len(self.strings)):
            st.int_(i)
            st.str_(self.strings[i])
            self.cnt[S_STRINGS] += 1
        self.sent = len(self.strings)
        payload = Buf()
        payload.b += _STAMP.pack(*self.stamp)
        start = self.pos
        for sid in FILE_ORDER:
            if not self.cnt[sid]:
                continue
            body = Buf()
            if sid != S_VIRTUAL:
                body.var(self.cnt[sid])
            entries_at = len(payload.b) + 5 + len(body.b)
            body.b += self.sec[sid].b
            payload.u8(sid)
            payload.u32(len(body.b))
            if sid == S_PARTICLES:
                for oid, rel in self.pframes:
                    self.frames.append((oid, self.cur, start + 8 + entries_at + rel))
            payload.b += body.b
        self.offsets.append(self._block(TAG_SAMPLE, payload.b))
        for sid in FILE_ORDER:
            if sid != S_STRINGS:
                self.expected.extend(self.pend[sid])
        self.strings_at.append(len(self.strings))
        self.sample_stats.append(dict(self.stat, bytes=self.pos - start))
        self.samples += 1
        self.cur = -1

    def close(self) -> None:
        b = Buf()
        b.var(len(self.offsets))
        for off in self.offsets:
            b.u64(off)
        b.var(len(self.keys))
        for first, last, keys in self.keys:
            b.var(first if keys else 0)
            b.var(last if keys else 0)
            b.var(keys)
        per: dict[int, list[tuple[int, int]]] = {}
        for oid, sample, off in self.frames:
            per.setdefault(oid, []).append((sample, off))
        b.var(len(per))
        for oid in sorted(per):
            b.var(oid)
            b.var(len(per[oid]))
            for sample, off in per[oid]:
                b.var(sample)
                b.u64(off)
        b.strs(self.strings)
        self._block(TAG_INDEX, b.b)
        end = Buf()
        end.u64(self.pos + 24)
        end.u32(self.samples)
        self._block(TAG_END, end.b)
        self.f.close()
        self.expected.append(self.end_event(self.samples, False, len(self.strings)))

    def end_event(self, samples: int, truncated: bool, strings: int) -> list[Any]:
        count = sum(1 for o in self.objects if o.get("addedAt", -1) < samples)
        stats = [[-1, -1, 0] for _ in range(count)]
        for sample, oid in self.touches:
            if sample < samples:
                x = stats[oid]
                if x[2] == 0:
                    x[0] = sample
                x[1] = sample
                x[2] += 1
        return ["End", samples, 1 if truncated else 0, self.strings[:strings], stats]


# ------------------------------------------------------------------ synth

def quat_axis(axis: Sequence[float], angle: float) -> tuple[float, float, float, float]:
    s = math.sin(angle / 2)
    return (axis[0] * s, axis[1] * s, axis[2] * s, math.cos(angle / 2))


def rot_angle(q: Sequence[float], p: Sequence[float]) -> float:
    if sum(a * b for a, b in zip(q, p)) < 0:
        p = [-v for v in p]
    d = math.sqrt(sum((a - b) ** 2 for a, b in zip(q, p)))
    return 4 * math.asin(min(1.0, d / 2))


class KeyRule:
    """The recorder's change rule (2.3.2): key when the change since the last key exceeds eps, with a pre-key (values of
    the previous sample) when the previous sample had no key."""

    def __init__(self, eps: float, rotation: bool) -> None:
        self.eps = eps
        self.rotation = rotation
        self.last: Sequence[float] | None = None
        self.last_sample = -1
        self.prev: Sequence[float] | None = None

    def step(self, k: int, value: Sequence[float]) -> list[tuple[bool, Sequence[float]]]:
        value = tuple(f32(v) for v in value)
        out: list[tuple[bool, Sequence[float]]] = []
        if self.last is None:
            out.append((False, value))
        else:
            change = rot_angle(value, self.last) if self.rotation else math.dist(value, self.last)
            if change > self.eps:
                if self.last_sample < k - 1 and self.prev is not None:
                    out.append((True, self.prev))
                out.append((False, value))
        if out:
            self.last = value
            self.last_sample = k
        self.prev = value
        return out


def particle_raw(rng: random.Random, n: int, t: float) -> bytes:
    out = bytearray()
    for i in range(n):
        life = 2.0 - (i * 0.05) % 2.0
        out += _PARTICLE.pack(math.sin(i + t), 0.1 * i, math.cos(i - t), 0.0, 1.0 + 0.01 * i, 0.0, 0.0, -0.1, 0.0,
                              0.0, 0.0, 1.0, (i * 0.3) % 6.28, 0.5, 0.05 + 0.001 * i,
                              200, 180, 120 + i % 100, 255, rng.getrandbits(32), life, 2.0, 0.0, 0.0)
    return bytes(out)


SYNTH_HEADER = {"version": 1, "created": "synthetic", "spec": "srec_tool synth", "level": "synthetic",
                "recorderVersion": "srec_tool synth 1", "clock": "real", "captureFramerate": 0, "subscene": 7,
                "savepoint": 0, "streamVersion": 1, "screenW": 1280, "screenH": 720, "fixedDeltaTime": 1.0 / 60.0,
                "t0": 100.0, "t0Real": 50.0, "stepAtT0": 6000, "gameFov": 30.0, "subscenes": ["#synth_Gameplay"],
                "keptCullers": ["Synth_CULL"], "forcedCullers": ["Synth_CULL"], "fogFields": ["fogColor", "fogDensity"],
                "postFields": [], "cameraDriven": ["Synth/LensFlare"], "posEps": 1e-4, "rotEps": 0.0012, "cut": 0.75,
                "vSyncCount": 1, "targetFrameRate": -1, "refreshRate": 60}


def synth_objects() -> list[dict[str, Any]]:
    rb, col, ren, mi, ps, li, act, lr = 1, 4096, 32, 128, 8, 16, 2048, 1024
    table = [
        make_object(0, K_TRANSFORM, "Synth", comps=rb | col | ren, extent=0.5, instanceId=1000),
        make_object(1, K_TRANSFORM, "Synth/Arm", parentId=0, flags=OBJ_LOCAL, comps=ren, extent=0.5, instanceId=1001,
                    pos=(0.0, 1.0, 0.0)),
        make_object(2, K_TRANSFORM, "Synth/Arm/Hand", parentId=1, flags=OBJ_LOCAL, comps=ren, extent=0.2,
                    instanceId=1002, pos=(0.0, 0.5, 0.0)),
        make_object(3, K_TRANSFORM, "Synth/Crane", comps=ren, extent=5.0, instanceId=1003, pos=(4.0, 0.0, 2.0)),
        make_object(4, K_TRANSFORM, "Synth/Door", comps=ren | col, extent=1.0, instanceId=1004, pos=(-3.0, 0.0, 1.0)),
        make_object(5, K_TRANSFORM, "Synth/Chick", comps=rb | col | ren, extent=0.15, instanceId=1005,
                    types=["Chicken"]),
        make_object(6, K_LIGHT, "Synth/Lamp", comps=li, lightType=2, lightShadows=1, instanceId=1006),
        make_object(7, K_MATERIAL, "Synth/Lamp/Glow", parentId=6, comps=mi, instanceId=1007,
                    floatProps=["_Fade", "_PDTime"], colorProps=["_Color"], vectorProps=["_Wind"],
                    textureProps=["_MainTex"]),
        make_object(8, K_PARTICLES, "Synth/FX", comps=ps, maxParticles=32, simulationSpace=1, randomSeed=1234,
                    instanceId=1008),
        make_object(9, K_PROBE, "_Boy/Probe", instanceId=1009, comps=512),
        make_object(10, K_ROPE, "Synth/CraneRope", comps=lr, ropeVertices=15, ropeDrawMode=1, ropeLineDivisions=4,
                    instanceId=1010),
        make_object(11, K_RENDERER, "Synth/Mesh", parentId=0, comps=ren, materialSlots=2, instanceId=1011),
        make_object(12, K_TRANSFORM, "Synth/Culler", flags=OBJ_CAMDRIVEN | OBJ_SEENACTIVE, instanceId=1012,
                    types=["BoundsCullerRuntime"]),
        make_object(13, K_BOYBONE, "_Boy/Skeleton/SpineB", owner="boy:bone:SpineB", comps=act, extent=0.3,
                    instanceId=1013),
        make_object(14, K_CLOTHBONE, "Huddle/cloth/3", owner="huddle:cloth:3", extent=0.3, instanceId=1014),
        make_object(15, K_TRANSFORM, "Synth/LensFlare", flags=OBJ_CAMXF, instanceId=1015, types=["LensFlareSimple"]),
        make_object(16, K_TRANSFORM, "_Dynamics", instanceId=1016),
    ]
    for o in table:
        depth = o["path"].count("/")
        o["ordinals"] = [0] * (depth + 1)
        o["siblings"] = [o["id"] % 5] * (depth + 1)
    return table


def synth_virtual(t: float, k: int, idle: bool) -> dict[str, Any]:
    cam = (2.0 * math.sin(0.2 * t), 1.5, -10.0 + 0.1 * t)
    rot = quat_axis((0.0, 1.0, 0.0), 0.05 * math.sin(t))
    boy = (0.8 * t, 0.0, 0.0)
    pose = {"p": cam, "r": rot, "fov": 30.0, "aim": boy, "near": 0.3}
    game = {"ok": True, "step": k, "rendered": pose, "clean": dict(pose, near=0.3), "positionCamera": cam,
            "positionAim": boy, "positionUp": (0.0, 1.0, 0.0), "velocity": (0.8, 0.0, 0.1), "aimDistance": 11.0,
            "box": "SynthBox", "probe": (boy[0], 0.5, 0.0), "extrapolation": 0.0, "handOffset": (0.0, 0.0, 0.0),
            "handRot": (0.0, 0.0, 0.0, 1.0), "shakeOffset": (0.0, 0.0, 0.0), "shakeRot": (0.0, 0.0, 0.0, 1.0),
            "cut": False}
    return {"game": game, "camPos": cam, "camRot": rot, "camFov": 30.0, "boyPos3": boy, "boyVel3": (0.8, 0.0, 0.0),
            "boyState": "BoyIdleState" if idle else "BoyRunState", "boyVisual": "VisualBoyRun", "huddlePos3": (0.0, 0.0, 0.0),
            "huddleVel3": (0.0, 0.0, 0.0), "huddleState": "", "probe": (boy[0], 0.5, 0.0), "flags": VIRT_BOY}


def synth(out_dir: Path, samples: int = 240, rate: int = 60, seed: int = 1, jitter: float = 0.0, hitch: bool = False,
          sidecars: bool = True, truth: bool = False, name: str = "scene.srec", shader: bool = True) -> Writer:
    out_dir.mkdir(parents=True, exist_ok=True)
    rng = random.Random(seed)
    header = dict(SYNTH_HEADER, version=CONTENT_VERSION if shader else 1)
    w = Writer(out_dir / name, header)
    table = synth_objects()
    w.set_objects(table)
    for s in ("cookie_a", "tex_a", "mat_a", "mat_b"):
        w.string_id(s)
    n = samples
    crane = KeyRule(f32(max(2e-6, min(header["posEps"] / 5.0, header["rotEps"]))), True)
    door = KeyRule(f32(header["posEps"]), False)
    spawn_at, delete_at, cut_at = n // 3, (2 * n) // 3, n // 2
    fall_at, land_at, prop_at = round(0.4 * n), round(0.6 * n), n // 4
    fx_from, fx_to = n // 5, (2 * n) // 5
    hitch_at = n // 3 + 1 if hitch else -1
    t = 0.0
    step = header["stepAtT0"]
    for k in range(n):
        if k > 0:
            dt = 0.1 if k == hitch_at else (1.0 / rate) * (1.0 + rng.uniform(-jitter, jitter) if jitter else 1.0)
            t += dt
        logic = header["stepAtT0"] + int(math.floor(t * 60.0 + 1e-6))
        fixed, step = logic - step, logic
        flags = (SMPL_MARKER if k % 30 == 0 else 0) | (SMPL_GAMEFRAME if k % 10 == 0 else 0) | (SMPL_HITCH if k == hitch_at else 0)
        w.begin_sample(t, header["t0Real"] + t + 0.0005 * math.sin(k), logic, 5000 + k, fixed, flags)
        full = k == 0
        trs = XF_POS | XF_ROT | XF_SCALE
        if k == spawn_at:
            w.obj_add(make_object(17, K_SPAWNED, "_Dynamics/Spark(Clone)", parentId=16, flags=OBJ_SPAWNED | OBJ_LOCAL,
                                  instanceId=-2017, comps=32, extent=0.1, ordinals=[0, 0], siblings=[0, 0]))
        if k == prop_at:
            w.mat_prop(7, MAT_FLOAT, "_Wet")
        w.xf(0, trs if full else XF_POS | XF_ROT, (2.0 * math.sin(t), 0.5 * t, 0.0),
             quat_axis((0.0, 1.0, 0.0), 0.3 * t), (1.0, 1.0, 1.0))
        w.xf(1, trs if full else XF_ROT, (0.0, 1.0, 0.0), quat_axis((0.0, 0.0, 1.0), 0.8 * math.sin(2 * t)), (1.0, 1.0, 1.0))
        if full:
            w.xf(2, trs, (0.0, 0.5, 0.0), (0.0, 0.0, 0.0, 1.0), (1.0, 1.0, 1.0))
        for pre, q in crane.step(k, quat_axis((0.0, 1.0, 0.0), 5e-6 * k)):
            w.xf(3, (trs if full else XF_ROT) | (XF_PREKEY if pre else 0), (4.0, 0.0, 2.0), q, (1.0, 1.0, 1.0))
        for pre, p in door.step(k, (-3.0 + 3e-5 * k, 0.0, 1.0)):
            w.xf(4, (trs if full else XF_POS) | (XF_PREKEY if pre else 0), p, (0.0, 0.0, 0.0, 1.0), (1.0, 1.0, 1.0))
        chick = (1.0 * t, 0.1, 3.0 if k >= cut_at else 0.0)
        w.xf(5, (trs if full else XF_POS) | (XF_CUT if k == cut_at else 0), chick, (0.0, 0.0, 0.0, 1.0), (1.0, 1.0, 1.0))
        if full:
            w.xf(12, trs, (0.0, 0.0, 5.0), (0.0, 0.0, 0.0, 1.0), (1.0, 1.0, 1.0))
        w.xf(13, trs if full else XF_POS | XF_ROT, (0.8 * t, 0.9, 0.0), quat_axis((1.0, 0.0, 0.0), 0.2 * math.sin(3 * t)),
             (1.0, 1.0, 1.0))
        if full or k % 2 == 0:
            w.xf(14, trs if full else XF_POS, (5.0 + 0.2 * math.sin(t), 0.4, 1.0), (0.0, 0.0, 0.0, 1.0), (1.0, 1.0, 1.0))
        if full:
            w.xf(15, trs, (0.0, 3.0, -5.0), (0.0, 0.0, 0.0, 1.0), (1.0, 1.0, 1.0))
            w.xf(16, trs, (0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0), (1.0, 1.0, 1.0))
        if spawn_at <= k < delete_at:
            w.xf(17, trs if k == spawn_at else XF_POS, (0.0, 0.02 * (k - spawn_at), 0.0), (0.0, 0.0, 0.0, 1.0),
                 (1.0, 1.0, 1.0))
        lamp_off = n // 3 <= k < n // 3 + 20
        mesh_off = (2 * n) // 3 <= k < (2 * n) // 3 + 10
        if full:
            for o in table:
                bits = FL_ACTIVE
                if o["kind"] == K_RENDERER:
                    bits |= FL_RENDERER
                if o["kind"] == K_LIGHT:
                    bits |= FL_LIGHT
                if o["id"] == 5:
                    bits |= FL_KINEMATIC
                w.flags(o["id"], bits)
        else:
            if k == cut_at:
                w.flags(5, FL_ACTIVE)
            if k in (n // 3, n // 3 + 20):
                w.flags(6, FL_ACTIVE | (0 if lamp_off else FL_LIGHT))
            if k in ((2 * n) // 3, (2 * n) // 3 + 10):
                w.flags(11, FL_ACTIVE | (0 if mesh_off else FL_RENDERER))
            if k == spawn_at:
                w.flags(17, FL_ACTIVE)
        if fx_from <= k <= fx_to:
            count = 0 if k == fx_to else min(32, k - fx_from + 1)
            w.particles(8, particle_raw(rng, count, t))
        if full:
            w.light(6, 0xFF, {"enabled": True, "intensity": 2.0, "color": (1.0, 0.9, 0.7, 1.0), "range": 12.0,
                              "spotAngle": 45.0, "cookie": w.string_id("cookie_a"), "useStaticShadows": False,
                              "staticShadowsDirty": False})
        elif k % 10 == 0 or k in (n // 3, n // 3 + 20):
            w.light(6, LI_ENABLED | LI_INTENSITY, {"enabled": not lamp_off, "intensity": 2.0 + 0.5 * math.sin(k)})
        if full:
            w.material(7, MAT_FLOAT, 0, (1.0, 0, 0, 0))
        w.material(7, MAT_FLOAT, 1, (t, 0, 0, 0))
        if full:
            w.material(7, MAT_COLOR, 0, (1.0, 1.0, 1.0, 1.0))
            w.material(7, MAT_VECTOR, 0, (0.1, 0.0, 0.2, 0.0))
            w.material(7, MAT_TEXTURE, 0, texture=w.string_id("tex_a"))
        if prop_at <= k <= n // 2:
            w.material(7, MAT_FLOAT, 0, (1.0 - (k - prop_at) / max(1, n // 2 - prop_at), 0, 0, 0))
        if k >= prop_at:
            w.material(7, MAT_FLOAT, 2, (0.5 + 0.5 * math.sin(t), 0, 0, 0))
        if k == n // 2:
            w.material(7, MAT_COLOR, 0, (1.0, 0.2, 0.2, 1.0))
            w.material(7, MAT_TEXTURE, 0, texture=w.string_id("tex_b"))
        w.virtual(synth_virtual(t, k, t < 1.0))
        if full or k % 2 == 0:
            w.probe(9, (0.8 * t, 0.5, 0.0))
        if full or k % 30 == 0:
            w.render(0, VAL_VEC4, (0.5, 0.55, 0.6, 1.0 - 0.001 * k))
            w.render(1, VAL_FLOAT, (0.02 + 0.0001 * k, 0, 0, 0))
        if k == 0:
            w.event("setup", "seed", str(seed))
        if k == 30 % n:
            w.event("cue", "suction", "call ChickenGunObject/ChickenGunVacuumLogic ChickenGunLogic ToggleSuctionOn")
        if k == fall_at:
            w.event("event", "baleFall", "signal HayBaleHolder/HayBaleTrigger enterSignal")
        if k == land_at:
            w.event("event", "baleLand", "impact HayBaleHolder/HayBale")
        if k == n - 1:
            w.event("mark", "end", "")
        if full or k % 30 == 0:
            w.cam_active(12, (k // 30) % 2 == 0)
        swing = 0.3 * math.sin(1.5 * t)
        w.rope(10, [(4.0 + swing * i / 14.0, 6.0 - 0.4 * i, 2.0) for i in range(15)])
        if full:
            w.shared_mat(11, 0, w.string_id("mat_a"))
            w.shared_mat(11, 1, w.string_id("mat_b"))
        elif k == n // 3:
            w.shared_mat(11, 0, w.string_id("mat_c"))
            w.shared_mat(11, 1, w.string_id("mat_a"))
        if shader:
            synth_shader(w, k, n, t, prop_at, land_at)
        if k == delete_at and spawn_at < delete_at:
            w.obj_del(17)
        w.end_sample()
    w.close()
    if sidecars:
        write_sidecars(out_dir, w, name)
    if truth:
        (out_dir / "truth.json").write_text(json.dumps({"events": w.expected, "sample_starts": w.sample_starts,
                                                        "strings_at": w.strings_at}, ensure_ascii=False) + "\n",
                                            encoding="utf-8")
    return w


def synth_shader(w: Writer, k: int, n: int, t: float, def_at: int, land_at: int) -> None:
    """Shader channels as the recorder writes them (change-only, every channel in sample 0): a ramping global float, a
    stepping global colour, a bruise vector on a shared material (an impact at land_at), a property-block vector on the
    renderer object 11 and a global vector defined mid-take at def_at."""
    if k == 0:
        w.shader_def(SH_GLOBAL, -1, VAL_FLOAT, "_PDHuddleToon")
        w.shader_def(SH_GLOBAL, -1, VAL_VEC4, "_PDHuddleAmbient")
        w.shader_def(SH_MATERIAL, w.string_id("CHARACTER_Huddle_2"), VAL_VEC4, "dam_top")
        w.shader_def(SH_BLOCK, 11, VAL_VEC4, "_DamageLimb")
    if k == def_at:
        w.shader_def(SH_GLOBAL, -1, VAL_VEC4, "_Player2GrassPos")
    ramp_from = n // 2
    if k == 0 or ramp_from < k <= ramp_from + 30:
        w.shader_val(0, (min(1.0, max(0.0, (k - ramp_from) / 30.0)), 0.0, 0.0, 0.0))
    if k % 20 == 0:
        level = 0.2 + 0.05 * (k // 20)
        w.shader_val(1, (level, level * 0.9, level * 0.8, 1.0))
    if k == 0 or k == land_at:
        w.shader_val(2, (0.0 if k == 0 else 0.35, 0.0, 0.0, 0.0))
    if k % 10 == 0:
        w.shader_val(3, (0.01 * k, 0.0, 0.5 if k >= land_at else 0.0, 0.0))
    if k >= def_at:
        w.shader_val(4, (0.8 * t, 0.0, 0.0, 1.0))


def comp_names(mask: int) -> str:
    return ",".join(n for i, n in enumerate(COMP_NAMES) if mask & (1 << i))


def write_sidecars(out_dir: Path, w: Writer, name: str) -> None:
    rec = read_srec(out_dir / name)
    write_objects_tsv(out_dir / "objects.tsv", rec)
    t0r = w.header["t0Real"]
    with (out_dir / "record_meta.csv").open("w", newline="", encoding="utf-8") as f:
        cw = csv.writer(f, lineterminator="\n")
        cw.writerow(["sample", "frame_count", "game_time", "dt", "real_time", "logic_step", "fixed_steps", "time_scale",
                     "flags", "xf_keys", "particles", "bytes"])
        prev = None
        for st in rec.stamps:
            dt = st["gameTime"] - prev if prev is not None else 0.0
            prev = st["gameTime"]
            cw.writerow([st["sample"], st["frameCount"], f"{st['gameTime']:.6f}", f"{dt:.6f}",
                         f"{st['realTime'] - t0r:.6f}", st["logicStep"], st["fixedSteps"], 1, st["flags"], st["xf"],
                         st["particles"], st["bytes"]])
    full = read_srec(out_dir / name, events=True)
    with (out_dir / "events.csv").open("w", newline="", encoding="utf-8") as f:
        cw = csv.writer(f, lineterminator="\n")
        cw.writerow(["step", "game_time", "frame", "kind", "name", "source", "detail"])
        for e in full.events or []:
            if e[0] == "Event":
                st = full.stamps[e[1]]
                cw.writerow([e[1], f"{st['gameTime']:.6f}", st["frameCount"], e[2], e[3], "srec", e[4]])
    duration = rec.stamps[-1]["gameTime"] if rec.stamps else 0.0
    meta = {"synthetic": True, "file": name, "header": rec.header, "samples": rec.samples, "objects": len(rec.objects),
            "duration": duration, "bytes": rec.size, "runnerPhase": "afterLateUpdate", "holds": [],
            "pacing": {"vSyncCount": 1, "targetFrameRate": -1, "refreshRate": 60, "changes": 0},
            "audio": {"usable": False, "reason": "synthetic recording, no capture"}}
    (out_dir / "record.json").write_text(json.dumps(meta, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")


# ------------------------------------------------------------------ commands

def write_objects_tsv(path: Path | None, rec: Recording, flt: str = "", kind: str = "") -> int:
    rows = ["\t".join(("id", "kind", "path", "owner", "comps", "first", "last", "keys", "flags", "parent", "depth",
                       "added", "deleted", "types"))]
    for o in rec.objects:
        kname = KIND_NAMES[o["kind"]] if o["kind"] < len(KIND_NAMES) else str(o["kind"])
        if kind and kind not in (kname, str(o["kind"])):
            continue
        if flt and flt.lower() not in o["path"].lower() and flt.lower() not in o["owner"].lower():
            continue
        fl = ",".join(n for i, n in enumerate(OBJ_FLAG_NAMES) if o["flags"] & (1 << i))
        rows.append("\t".join(str(v) for v in (o["id"], kname, o["path"], o["owner"], comp_names(o["comps"]), o["first"],
                                               o["last"], o["keys"], fl, o["parentId"], o["depth"], o["addedAt"],
                                               o["deletedAt"], ",".join(o["types"]))))
    text = "\n".join(rows) + "\n"
    if path is None:
        sys.stdout.write(text)
    else:
        path.write_text(text, encoding="utf-8")
    return len(rows) - 1


def summary(rec: Recording) -> dict[str, Any]:
    st = rec.stamps
    dts = [b["gameTime"] - a["gameTime"] for a, b in zip(st, st[1:])]
    kinds = Counter(KIND_NAMES[o["kind"]] if o["kind"] < len(KIND_NAMES) else str(o["kind"]) for o in rec.objects)
    xf = sum(s["xf"] for s in st)
    pbytes = sum(s["pbytes"] for s in st)
    est = xf * SIZE_XF_BYTES + pbytes + SIZE_SAMPLE_BYTES * len(st)
    h = rec.header or {}
    return {"file": str(rec.path), "bytes": rec.size, "readable": rec.readable, "version": rec.version,
            "complete": rec.complete, "truncated": rec.truncated, "stop": rec.stop, "stopKind": rec.stop_kind,
            "header": h, "samples": len(st),
            "duration": st[-1]["gameTime"] - st[0]["gameTime"] if st else 0.0,
            "gameTime": [st[0]["gameTime"], st[-1]["gameTime"]] if st else [],
            "realSpan": st[-1]["realTime"] - st[0]["realTime"] if st else 0.0,
            "dt": {"mean": sum(dts) / len(dts), "min": min(dts), "max": max(dts)} if dts else {},
            "hitches": sum(1 for s in st if s["flags"] & SMPL_HITCH),
            "holds": sum(1 for s in st if s["flags"] & SMPL_HOLD),
            "markers": sum(1 for s in st if s["flags"] & SMPL_MARKER),
            "gameFrames": sum(1 for s in st if s["flags"] & SMPL_GAMEFRAME),
            "objects": len(rec.objects), "objectsAdded": sum(1 for o in rec.objects if o["addedAt"] >= 0),
            "objectsDeleted": sum(1 for o in rec.objects if o["deletedAt"] >= 0), "kinds": dict(kinds),
            "keys": sum(o["keys"] for o in rec.objects), "xfKeys": xf,
            "sections": {SECTION_NAMES.get(k, str(k)): {"entries": rec.section_entries[k], "bytes": rec.section_bytes[k]}
                         for k in sorted(rec.section_bytes)},
            "particles": {"systems": len(rec.frames), "frames": sum(len(v) for v in rec.frames.values()),
                          "count": sum(s["particles"] for s in st), "bytes": pbytes},
            "strings": len(rec.strings), "index": rec.index is not None,
            "shaderChannels": len(rec.shader),
            "shader": {"scopes": dict(Counter(SH_SCOPE_NAMES[d["scope"]] for d in rec.shader)),
                       "keys": sum(d["keys"] for d in rec.shader),
                       "names": [f"{SH_SCOPE_NAMES[d['scope']]}:{d['name']}" for d in rec.shader]},
            "sizeFormula": {"estimate": est, "ratio": rec.size / est if est else 0.0},
            "problems": rec.problems, "warnings": rec.warnings}


def cmd_info(args: argparse.Namespace) -> int:
    rec = read_srec(Path(args.path))
    if rec.fatal:
        print(f"{rec.path}: {rec.fatal}", file=sys.stderr)
        return 2
    s = summary(rec)
    if args.json:
        print(json.dumps(s, indent=1, ensure_ascii=False))
        return 0
    h = s["header"]
    state = "complete" if rec.complete else f"TRUNCATED ({rec.stop})"
    print(f"file      {rec.path}  {rec.size / 1048576:.2f} MB  version {rec.version}  {state}")
    print(f"header    level {h.get('level')!r}  clock {h.get('clock')}  created {h.get('created')!r}  "
          f"screen {h.get('screenW')}x{h.get('screenH')}  t0 {h.get('t0')}  t0Real {h.get('t0Real')}  "
          f"subscene {h.get('subscene')}/{h.get('savepoint')}")
    print(f"          posEps {h.get('posEps')}  rotEps {h.get('rotEps')}  cut {h.get('cut')}  vSync {h.get('vSyncCount')}  "
          f"targetFrameRate {h.get('targetFrameRate')}  refresh {h.get('refreshRate')}  fogFields {h.get('fogFields')}")
    dt = s["dt"]
    print(f"samples   {s['samples']}  duration {s['duration']:.3f} s  real span {s['realSpan']:.3f} s" +
          (f"  dt mean {dt['mean'] * 1000:.2f} ms min {dt['min'] * 1000:.2f} max {dt['max'] * 1000:.2f}" if dt else "") +
          f"  hitches {s['hitches']}  holds {s['holds']}  markers {s['markers']}  game frames {s['gameFrames']}")
    print(f"objects   {s['objects']} (OBJA {s['objectsAdded']}, OBJD {s['objectsDeleted']})  " +
          "  ".join(f"{k} {v}" for k, v in sorted(s["kinds"].items())))
    print(f"keys      {s['keys']} (xf {s['xfKeys']})  strings {s['strings']}  index {'yes' if s['index'] else 'no'}")
    p = s["particles"]
    print(f"particles {p['systems']} systems  {p['frames']} frames  {p['count']} particles  {p['bytes'] / 1048576:.2f} MB")
    if s["shaderChannels"]:
        sh = s["shader"]
        print(f"shader    {s['shaderChannels']} channels  {sh['keys']} keys  " +
              "  ".join(f"{k} {v}" for k, v in sorted(sh["scopes"].items())) + "  " + ", ".join(sh["names"][:12]))
    for name, v in s["sections"].items():
        print(f"  section {name:<10} {v['entries']:>9} entries  {v['bytes'] / 1024:>10.1f} KB")
    sf = s["sizeFormula"]
    print(f"size      formula of 4.4 {sf['estimate'] / 1048576:.2f} MB  ratio {sf['ratio']:.2f}")
    for line in rec.problems:
        print(f"problem   {line}")
    for line in rec.warnings[:20]:
        print(f"warning   {line}")
    return 0


def sidecar_rows(path: Path) -> int | None:
    if not path.is_file():
        return None
    with path.open(encoding="utf-8") as f:
        return max(0, sum(1 for line in f if line.strip()) - 1)


def cmd_verify(args: argparse.Namespace) -> int:
    rec = read_srec(Path(args.path))
    if rec.fatal:
        print(f"FAIL {rec.path}: {rec.fatal}")
        return 2
    problems = list(rec.problems)
    notes: list[str] = []
    if rec.truncated:
        line = f"truncated: readable prefix of {rec.samples} samples, {rec.readable} of {rec.size} bytes ({rec.stop})"
        (problems if args.strict else notes).append(line)
    for side, n in (("record_meta.csv", rec.samples), ("objects.tsv", len(rec.objects))):
        rows = sidecar_rows(rec.path.parent / side)
        if rows is None:
            notes.append(f"no {side}")
        elif rows != n:
            (notes if rec.truncated else problems).append(f"{side}: {rows} rows, recording has {n}")
        else:
            notes.append(f"{side}: {rows} rows match")
    s = summary(rec)
    ratio = s["sizeFormula"]["ratio"]
    line = f"size {rec.size} bytes vs 4.4 formula {s['sizeFormula']['estimate']} bytes: ratio {ratio:.2f}"
    if args.size and not 0.5 <= ratio <= 2.0:
        problems.append(line + " (outside 0.5-2)")
    else:
        notes.append(line)
    blocks = Counter(b[0] for b in rec.blocks)
    notes.append("blocks " + ", ".join(f"{k} {v}" for k, v in blocks.items()) + f"; all {len(rec.blocks)} CRCs ok")
    ok = not problems
    if args.json:
        print(json.dumps({"ok": ok, "file": str(rec.path), "samples": rec.samples, "objects": len(rec.objects),
                          "truncated": rec.truncated, "stop": rec.stop, "problems": problems, "notes": notes,
                          "warnings": rec.warnings}, indent=1, ensure_ascii=False))
    else:
        print(f"{'OK' if ok else 'FAIL'} {rec.path}: {rec.samples} samples, {len(rec.objects)} objects, "
              f"{'truncated prefix' if rec.truncated else 'complete'}")
        for p in problems:
            print(f"  problem: {p}")
        for n in notes:
            print(f"  note: {n}")
        for w in rec.warnings[:20]:
            print(f"  warning: {w}")
    return 0 if ok else 1


def cmd_objects(args: argparse.Namespace) -> int:
    rec = read_srec(Path(args.path))
    if rec.fatal:
        print(f"{rec.path}: {rec.fatal}", file=sys.stderr)
        return 2
    write_objects_tsv(None, rec, args.filter or "", args.kind or "")
    return 0


def fmt(v: Any) -> str:
    return repr(v) if isinstance(v, float) else str(v)


def cmd_export(args: argparse.Namespace) -> int:
    rec = read_srec(Path(args.path), events=True)
    if rec.fatal:
        print(f"{rec.path}: {rec.fatal}", file=sys.stderr)
        return 2
    what = args.what
    times = [s["gameTime"] for s in rec.stamps]
    rows: list[list[Any]] = []
    if what == "stamps":
        head = ["sample", "frame_count", "game_time", "dt", "real_time", "real_offset", "logic_step", "fixed_steps", "flags",
                "xf_keys", "particles", "bytes"]
        t0r = (rec.header or {}).get("t0Real", 0.0)
        prev = None
        for s in rec.stamps:
            rows.append([s["sample"], s["frameCount"], s["gameTime"], 0.0 if prev is None else s["gameTime"] - prev,
                         s["realTime"], s["realTime"] - t0r, s["logicStep"], s["fixedSteps"], s["flags"], s["xf"],
                         s["particles"], s["bytes"]])
            prev = s["gameTime"]
    elif what == "events":
        head = ["sample", "game_time", "kind", "name", "detail"]
        rows = [[e[1], times[e[1]], e[2], e[3], e[4]] for e in rec.events or [] if e[0] == "Event"]
    elif what == "virtual":
        head = ["sample", "game_time"] + VIRTUAL_COLUMNS
        rows = [[e[1], times[e[1]], *e[2:]] for e in rec.events or [] if e[0] == "Virtual"]
    else:
        try:
            oid = int(what)
        except ValueError:
            print(f"export: '{what}' is not an object id, virtual, stamps or events", file=sys.stderr)
            return 2
        if not 0 <= oid < len(rec.objects):
            print(f"export: object {oid} not in the table ({len(rec.objects)} objects)", file=sys.stderr)
            return 2
        props = {k: list(rec.objects[oid][f"{n}Props"]) for k, n in enumerate(MAT_NAMES)}
        head = ["sample", "game_time", "channel", "bits", "px", "py", "pz", "qx", "qy", "qz", "qw", "sx", "sy", "sz",
                "parent", "value"]
        for e in rec.events or []:
            kind = e[0]
            if kind in ("Begin", "End", "Sample", "Virtual", "Render", "Event"):
                continue
            if kind == "ObjAdd":
                if e[2][0] == oid:
                    rows.append([e[1], times[e[1]], "objAdd", "", *[""] * 11, ""])
                continue
            if e[2] != oid:
                continue
            s = e[1]
            base = [s, times[s]]
            blank = [""] * 10
            if kind == "Xf":
                bits = e[3]
                pos = e[4:7] if bits & XF_POS else [""] * 3
                rot = e[7:11] if bits & XF_ROT else [""] * 4
                scl = e[11:14] if bits & XF_SCALE else [""] * 3
                tags = "|".join(n for b, n in ((XF_CUT, "cut"), (XF_PARENT, "parent"), (XF_PREKEY, "preKey")) if bits & b)
                rows.append(base + ["xf", bits, *pos, *rot, *scl, e[14], tags])
            elif kind == "Flags":
                rows.append(base + ["flags", e[3], *blank, "", "|".join(n for i, n in enumerate(FLAG_NAMES) if e[3] & (1 << i))])
            elif kind == "Particles":
                rows.append(base + ["particles", "", *blank, "", f"n={e[3]} crc={e[4]:08x}"])
            elif kind == "Light":
                mask = e[3]
                parts = []
                names = (("enabled", e[4]), ("intensity", e[5]), ("color", " ".join(fmt(v) for v in e[6:10])),
                         ("range", e[10]), ("spot", e[11]),
                         ("cookie", rec.strings[e[12]] if 0 <= e[12] < len(rec.strings) else e[12]),
                         ("static", e[13]), ("dirty", e[14]))
                for i, (n, v) in enumerate(names):
                    if mask & (1 << i):
                        parts.append(f"{n}={fmt(v)}")
                rows.append(base + ["light", mask, *blank, "", " ".join(parts)])
            elif kind == "MatProp":
                props[e[3]].append(e[4])
                rows.append(base + ["matProp", e[3], *blank, "", f"add {MAT_NAMES[e[3]]} {e[4]}"])
            elif kind == "Material":
                mk, prop = e[3], e[4]
                pname = props[mk][prop] if mk in props and prop < len(props[mk]) else f"#{prop}"
                if mk == MAT_TEXTURE:
                    val = rec.strings[e[9]] if 0 <= e[9] < len(rec.strings) else ""
                elif mk == MAT_FLOAT:
                    val = fmt(e[5])
                else:
                    val = " ".join(fmt(v) for v in e[5:9])
                rows.append(base + ["material", mk, *blank, "", f"{pname}={val}"])
            elif kind == "Probe":
                rows.append(base + ["probe", "", *e[3:6], *[""] * 7, "", ""])
            elif kind == "CamActive":
                rows.append(base + ["camActive", "", *blank, "", e[3]])
            elif kind == "Rope":
                rows.append(base + ["rope", "", *blank, "", f"n={e[3]} " + " ".join(fmt(v) for v in e[4])])
            elif kind == "SharedMat":
                name = rec.strings[e[4]] if 0 <= e[4] < len(rec.strings) else ""
                rows.append(base + ["sharedMat", "", *blank, "", f"slot={e[3]} material={name}"])
            elif kind == "ObjDel":
                rows.append(base + ["objDel", "", *blank, "", ""])
    out = open(args.out, "w", newline="", encoding="utf-8") if args.out else sys.stdout
    try:
        cw = csv.writer(out, lineterminator="\n")
        cw.writerow(head)
        for r in rows:
            cw.writerow([fmt(v) for v in r])
    finally:
        if args.out:
            out.close()
    if args.out:
        print(f"export {what}: {len(rows)} rows -> {args.out}")
    return 0


def dump_json(rec: Recording) -> str:
    events = rec.events or []
    body = ",\n".join(json.dumps(e, ensure_ascii=False) for e in events)
    return (json.dumps({"ok": not rec.fatal and not rec.problems, "read": not rec.fatal, "truncated": rec.truncated,
                        "problems": rec.problems, "warnings": rec.warnings})[:-1] + ', "events": [\n' + body + "\n]}\n")


def cmd_dump(args: argparse.Namespace) -> int:
    rec = read_srec(Path(args.path), events=True)
    if rec.fatal:
        print(f"{rec.path}: {rec.fatal}", file=sys.stderr)
        return 2
    text = dump_json(rec)
    if args.out:
        Path(args.out).write_text(text, encoding="utf-8")
        print(f"dump: {len(rec.events or [])} events -> {args.out}")
    else:
        sys.stdout.write(text)
    return 0


def cmd_synth(args: argparse.Namespace) -> int:
    if args.samples < 2:
        print("synth: --samples must be at least 2", file=sys.stderr)
        return 2
    out = Path(args.out_dir)
    w = synth(out, samples=args.samples, rate=args.rate, seed=args.seed, jitter=args.jitter, hitch=args.hitch,
              sidecars=not args.no_sidecars, truth=args.truth, name=args.name, shader=not args.no_shader)
    print(f"synth: {out / args.name}  {w.samples} samples, {len(w.objects)} objects, {w.pos} bytes, "
          f"{len(w.expected)} canonical events" + ("  (+ truth.json)" if args.truth else ""))
    return 0


# ------------------------------------------------------------------ record report (spec events and content checks)

TRANSFORM_KINDS = frozenset({K_TRANSFORM, K_BOYBONE, K_CLOTHBONE, K_SPAWNED})
_TIME_REF = re.compile(r"^@?([A-Za-z0-9_.]+)\s*([+-]\s*[0-9]+(?:\.[0-9]*)?)?$")


def spec_events(spec: dict[str, Any]) -> list[dict[str, Any]]:
    ev = spec.get("events") or []
    if isinstance(ev, dict):
        ev = [dict(v, name=k) for k, v in ev.items() if isinstance(v, dict) and not k.startswith("_")]
    return [e for e in ev if isinstance(e, dict) and e.get("name")]


def parse_check(text: str) -> tuple[str, str | None, str | None]:
    q, sep, win = text.partition(" @")
    q = q.strip()
    if not q:
        raise ValueError(f"check '{text}': empty query")
    if not sep:
        return q, None, None
    a, dots, b = win.strip().partition("..")
    if not dots or not a.strip() or not b.strip():
        raise ValueError(f"check '{text}': the window is ' @<from>..<to>' (seconds or event[+-offset])")
    return q, a.strip(), b.strip()


def time_of(text: str, firsts: dict[str, float]) -> float | None:
    t = text.strip()
    try:
        return float(t)
    except ValueError:
        pass
    m = _TIME_REF.match(t)
    if not m:
        return None
    base = firsts.get(m.group(1))
    return None if base is None else base + (float(m.group(2).replace(" ", "")) if m.group(2) else 0.0)


def span_union(a: list[tuple[int, int]], b: list[tuple[int, int]]) -> list[tuple[int, int]]:
    out: list[tuple[int, int]] = []
    for s, e in sorted(a + b):
        if out and s <= out[-1][1]:
            out[-1] = (out[-1][0], max(out[-1][1], e))
        elif e > s:
            out.append((s, e))
    return out


def span_cut(a: list[tuple[int, int]], b: list[tuple[int, int]]) -> list[tuple[int, int]]:
    out: list[tuple[int, int]] = []
    i = j = 0
    while i < len(a) and j < len(b):
        s, e = max(a[i][0], b[j][0]), min(a[i][1], b[j][1])
        if e > s:
            out.append((s, e))
        if a[i][1] < b[j][1]:
            i += 1
        else:
            j += 1
    return out


def bit_spans(changes: list[tuple[int, int]], initial: int, bit: int, alive: tuple[int, int]) -> list[tuple[int, int]]:
    out: list[tuple[int, int]] = []
    cur, since = bool(initial & bit), alive[0]
    for s, bits in sorted(changes):
        on = bool(bits & bit)
        if on == cur:
            continue
        if cur and s > since:
            out.append((since, s))
        cur, since = on, s
    if cur and alive[1] > since:
        out.append((since, alive[1]))
    return span_cut(out, [alive]) if out else []


class RecordFacts:
    def __init__(self, rec: Recording) -> None:
        self.rec = rec
        self.n = rec.samples
        self.times = [s["gameTime"] for s in rec.stamps]
        self.flags: dict[int, list[tuple[int, int]]] = {}
        self.rows: list[dict[str, Any]] = []
        for e in rec.events or []:
            if e[0] == "Flags":
                self.flags.setdefault(e[2], []).append((e[1], e[3]))
            elif e[0] == "Event":
                self.rows.append({"sample": e[1], "time": self.t(e[1]), "kind": e[2], "name": e[3], "detail": e[4]})
        self.by_path = {o["path"]: o["id"] for o in rec.objects if o["kind"] in TRANSFORM_KINDS}
        self.children: dict[int, list[int]] = {}
        for o in rec.objects:
            if o["kind"] not in TRANSFORM_KINDS:
                self.children.setdefault(o["parentId"], []).append(o["id"])
        self.firsts: dict[str, float] = {}
        for r in self.rows:
            self.firsts.setdefault(r["name"], r["time"])
        self.memo: dict[int, list[tuple[int, int]]] = {}

    def t(self, s: int) -> float:
        return self.times[min(max(s, 0), self.n - 1)] if self.n else 0.0

    def alive(self, o: dict[str, Any]) -> tuple[int, int]:
        a = max(0, o.get("addedAt", -1))
        d = o.get("deletedAt", -1)
        return a, d if d >= 0 else self.n

    def initial(self, o: dict[str, Any]) -> int:
        if not o["activeSelf"]:
            return 0
        return FL_RENDERER if o["kind"] == K_RENDERER else FL_LIGHT if o["kind"] == K_LIGHT else FL_ACTIVE

    def self_spans(self, oid: int, bit: int) -> list[tuple[int, int]]:
        o = self.rec.objects[oid]
        return bit_spans(self.flags.get(oid, []), self.initial(o), bit, self.alive(o))

    def ancestors(self, path: str) -> list[int]:
        out = []
        parts = path.split("/")
        for k in range(len(parts) - 1, 0, -1):
            oid = self.by_path.get("/".join(parts[:k]))
            if oid is not None:
                out.append(oid)
        return out

    def active_h(self, oid: int) -> list[tuple[int, int]]:
        hit = self.memo.get(oid)
        if hit is not None:
            return hit
        o = self.rec.objects[oid]
        spans = self.self_spans(oid, FL_ACTIVE)
        anc = self.ancestors(o["path"])
        if anc and spans:
            spans = span_cut(spans, self.active_h(anc[0]))
        self.memo[oid] = spans
        return spans

    def subtree(self, root: dict[str, Any]) -> list[int]:
        p = root["path"]
        return [o["id"] for o in self.rec.objects if o["kind"] in TRANSFORM_KINDS and (o["path"] == p or o["path"].startswith(p + "/"))]

    def visible(self, xf_ids: list[int]) -> tuple[list[tuple[int, int]], int]:
        out: list[tuple[int, int]] = []
        count = 0
        for x in xf_ids:
            for c in self.children.get(x, []):
                if self.rec.objects[c]["kind"] != K_RENDERER:
                    continue
                count += 1
                spans = span_cut(self.self_spans(c, FL_RENDERER), self.active_h(x))
                if spans:
                    out = span_union(out, spans)
        return out, count

    def match(self, q: str) -> list[dict[str, Any]]:
        objs = [o for o in self.rec.objects if o["kind"] in TRANSFORM_KINDS]
        if q.startswith("#"):
            return [o for o in objs if o["owner"] == "subscene:" + q or o["path"].split("/")[-1] == q[1:] + "_ContentRoot"]
        return [o for o in objs if o["path"] == q or o["path"].endswith("/" + q) or o["path"].split("/")[-1] == q]

    def span_text(self, spans: list[tuple[int, int]], limit: int = 8) -> list[list[float]]:
        return [[round(self.t(s), 3), round(self.t(e - 1), 3)] for s, e in spans[:limit]]


def record_check(f: RecordFacts, text: str, joins: list[Any]) -> dict[str, Any]:
    q, a, b = parse_check(text)
    out: dict[str, Any] = {"check": text, "query": q}
    hits = f.match(q)
    out["matches"] = len(hits)
    out["objects"] = [o["path"] for o in hits[:3]]
    if q.startswith("#"):
        h = f.rec.header or {}
        out["subscene"] = {"atStart": q in (h.get("subscenes") or []), "kept": q in (h.get("keptCullers") or []),
                           "joins": [j for j in joins if isinstance(j, dict) and q in json.dumps(j)][:5]}
    active: list[tuple[int, int]] = []
    xf: list[int] = []
    for o in hits[:200]:
        active = span_union(active, f.active_h(o["id"]))
        xf.extend(f.subtree(o))
    vis, rend = f.visible(sorted(set(xf))) if xf else ([], 0)
    out["renderers"] = rend
    out["active"] = f.span_text(active)
    out["visible"] = f.span_text(vis)
    out["firstActive"] = None if not active else {"sample": active[0][0], "time": round(f.t(active[0][0]), 4)}
    out["firstVisible"] = None if not vis else {"sample": vis[0][0], "time": round(f.t(vis[0][0]), 4)}
    verdict = "PASS" if hits and active else "FAIL"
    why = "" if hits else "no recorded object matches"
    if hits and not active:
        why = "never active in the recording"
    if a is not None and hits:
        t0, t1 = time_of(a, f.firsts), time_of(b, f.firsts)
        if t0 is None or t1 is None:
            verdict, why = "FAIL", f"window {a}..{b}: an event of the window never fired"
        else:
            need = vis if rend else active
            s0 = next((i for i, t in enumerate(f.times) if t >= t0 - 1e-6), f.n)
            s1 = next((i for i, t in enumerate(f.times) if t > t1 + 1e-6), f.n)
            out["window"] = [round(t0, 3), round(t1, 3)]
            got = span_cut([(s0, s1)], need) if s1 > s0 else []
            covered = sum(e - s for s, e in got)
            if s1 > s0 and covered < s1 - s0:
                missing = s0
                for s, e in got:
                    if s > missing:
                        break
                    missing = e
                verdict = "FAIL"
                why = f"{'visible' if rend else 'active'} only {covered}/{s1 - s0} samples of {t0:.2f}..{t1:.2f} s, first gap at {f.t(missing):.3f} s (sample {missing})"
    out["verdict"] = verdict
    if why:
        out["why"] = why
    return out


def record_report(rec: Recording, spec: dict[str, Any], checks: list[str], joins: list[Any]) -> dict[str, Any]:
    f = RecordFacts(rec)
    doc: dict[str, Any] = {"record": str(rec.path), "samples": f.n, "seconds": round(f.times[-1], 4) if f.times else 0.0,
                           "events": [dict(r, time=round(r["time"], 4)) for r in f.rows if r["kind"] != "srec"]}
    se = []
    for e in spec_events(spec):
        name, typ = e["name"], e.get("type", "")
        row: dict[str, Any] = {"name": name, "type": typ}
        if "expect" in e and isinstance(e["expect"], (int, float)):
            row["expect"] = e["expect"]
        first = next((r for r in f.rows if r["name"] == name), None)
        if typ == "posted" and e.get("match"):
            hits = [r for r in f.rows if fnmatch.fnmatchcase(r["name"], e["match"]) and r["kind"] != "srec"]
            row["posted"] = [{"name": r["name"], "sample": r["sample"], "time": round(r["time"], 4)} for r in hits]
            if first is None and hits:
                first = hits[0]
        elif typ == "posted" and first is None:
            first = next((r for r in f.rows if r["name"] == name), None)
        row["fired"] = first is not None
        if first is not None:
            row["sample"], row["time"] = first["sample"], round(first["time"], 4)
            if "expect" in row:
                row["delta"] = round(first["time"] - float(row["expect"]), 3)
        se.append(row)
    doc["specEvents"] = se
    doc["checks"] = [record_check(f, c, joins) for c in checks]
    doc["failed"] = sum(1 for c in doc["checks"] if c["verdict"] != "PASS")
    doc["unfired"] = [r["name"] for r in se if not r["fired"]]
    return doc


def report_text(doc: dict[str, Any]) -> str:
    lines = [f"RECORD REPORT {doc['record']}: {doc['samples']} samples, {doc['seconds']} s; {len(doc['checks'])} check(s), "
             f"{doc['failed']} FAIL; {len(doc['specEvents'])} spec event(s), {len(doc['unfired'])} not fired"]
    lines.append("spec events:")
    for r in doc["specEvents"]:
        if r["fired"]:
            d = f", expect {r['expect']} ({r['delta']:+.3f})" if "delta" in r else ""
            extra = f", {len(r['posted'])} posted: " + " ".join(f"{p['name']}@{p['time']:.2f}" for p in r["posted"][:12]) if r.get("posted") else ""
            lines.append(f"  ok      {r['name']:<22} sample {r['sample']:>6}  t {r['time']:8.3f}{d}{extra}")
        else:
            lines.append(f"  missing {r['name']:<22} ({r['type']})")
    lines.append("content checks:")
    for c in doc["checks"]:
        fa = c["firstActive"]
        fv = c["firstVisible"]
        lines.append(f"  {c['verdict']:<4} {c['check']}  matches {c['matches']}, renderers {c['renderers']}, first active "
                     f"{'-' if fa is None else str(fa['sample']) + ' / ' + format(fa['time'], '.3f') + ' s'}, first visible "
                     f"{'-' if fv is None else str(fv['sample']) + ' / ' + format(fv['time'], '.3f') + ' s'}"
                     + (f"; {c['why']}" if c.get("why") else ""))
        if c["active"]:
            lines.append("       active " + ", ".join(f"{a:.2f}-{b:.2f}" for a, b in c["active"]))
        if c["visible"]:
            lines.append("       visible " + ", ".join(f"{a:.2f}-{b:.2f}" for a, b in c["visible"]))
    quiet = Counter(r["kind"] for r in doc["events"] if r["kind"] in ("particleState",))
    lines.append(f"events in the recording: {len(doc['events'])}" + "".join(f", {n} {k} (JSON only)" for k, n in quiet.items()))
    for r in [r for r in doc["events"] if r["kind"] not in quiet][:400]:
        lines.append(f"  {r['sample']:>6} {r['time']:8.3f}  {r['kind']:<6} {r['name']}  {r['detail']}")
    return "\n".join(lines) + "\n"


def report_spec(path: Path, given: str | None) -> tuple[dict[str, Any], str]:
    cands = [Path(given)] if given else []
    folder = path if path.is_dir() else path.parent
    rj = folder / "record.json"
    if not given and rj.is_file():
        try:
            s = json.loads(rj.read_text(encoding="utf-8")).get("spec")
            if isinstance(s, str) and s:
                cands.append(Path(s))
        except ValueError:
            pass
    cands += [folder / "spec.json", folder.parent / "spec.json"]
    for c in cands:
        if c.is_file():
            return json.loads(c.read_text(encoding="utf-8")), str(c)
    if given:
        raise FileNotFoundError(given)
    return {}, ""


def cmd_report(args: argparse.Namespace) -> int:
    path = resolve(Path(args.path))
    spec, spec_path = report_spec(path, args.spec)
    checks = list(((spec.get("replay") or {}).get("record") or {}).get("checks") or []) + list(args.check or [])
    try:
        for c in checks:
            parse_check(c)
    except ValueError as e:
        print(f"report: {e}", file=sys.stderr)
        return 2
    rec = read_srec(path, events=True)
    if rec.fatal:
        print(f"{rec.path}: {rec.fatal}", file=sys.stderr)
        return 2
    joins: list[Any] = []
    rj = path.parent / "record.json"
    if rj.is_file():
        try:
            joins = list((json.loads(rj.read_text(encoding="utf-8")).get("subsceneJoins") or {}).get("log") or [])
        except ValueError:
            joins = []
    doc = record_report(rec, spec, checks, joins)
    doc["spec"] = spec_path
    out = Path(args.out) if args.out else path.parent / "record_report.json"
    out.write_text(json.dumps(doc, indent=1), encoding="utf-8")
    text = report_text(doc)
    out.with_suffix(".txt").write_text(text, encoding="utf-8")
    print(text, end="")
    print(f"wrote {out} and {out.with_suffix('.txt').name}")
    return 1 if doc["failed"] or (args.strict and doc["unfired"]) else 0


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="command", required=True)
    a = sub.add_parser("info", help="header, counts, sizes")
    a.add_argument("path")
    a.add_argument("--json", action="store_true")
    a.set_defaults(func=cmd_info)
    a = sub.add_parser("verify", help="integrity check (R2)")
    a.add_argument("path")
    a.add_argument("--strict", action="store_true", help="a truncated prefix fails")
    a.add_argument("--size", action="store_true", help="the 4.4 size formula ratio outside 0.5-2 fails")
    a.add_argument("--json", action="store_true")
    a.set_defaults(func=cmd_verify)
    a = sub.add_parser("objects", help="object table as TSV")
    a.add_argument("path")
    a.add_argument("filter", nargs="?")
    a.add_argument("--kind")
    a.set_defaults(func=cmd_objects)
    a = sub.add_parser("export", help="CSV of one object id, virtual, stamps or events")
    a.add_argument("path")
    a.add_argument("what")
    a.add_argument("--out")
    a.set_defaults(func=cmd_export)
    a = sub.add_parser("dump", help="canonical event list (JSON)")
    a.add_argument("path")
    a.add_argument("--out")
    a.set_defaults(func=cmd_dump)
    a = sub.add_parser("synth", help="synthetic recording for PLAY and COMPOSITOR tests")
    a.add_argument("out_dir")
    a.add_argument("--samples", type=int, default=240)
    a.add_argument("--rate", type=int, default=60)
    a.add_argument("--seed", type=int, default=1)
    a.add_argument("--jitter", type=float, default=0.0, help="relative sample-interval jitter (real clock), e.g. 0.1")
    a.add_argument("--hitch", action="store_true", help="one 0.1 s hitch sample")
    a.add_argument("--truth", action="store_true", help="also write truth.json (canonical events as written)")
    a.add_argument("--no-sidecars", action="store_true")
    a.add_argument("--no-shader", action="store_true", help="content version 1: no shader channels (sections 17/18)")
    a.add_argument("--name", default="scene.srec")
    a.set_defaults(func=cmd_synth)
    a = sub.add_parser("report", help="spec events and content checks per sample (record_report.json/.txt)")
    a.add_argument("path")
    a.add_argument("--spec", help="spec JSON (default: record.json spec, record/spec.json, take/spec.json)")
    a.add_argument("--check", action="append", help="extra content check: <query> [@<from>..<to>]")
    a.add_argument("--out", help="report JSON path (default <record>/record_report.json; .txt next to it)")
    a.add_argument("--strict", action="store_true", help="a spec event that never fired fails too")
    a.set_defaults(func=cmd_report)
    return p


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        return args.func(args)
    except FileNotFoundError as e:
        print(f"srec_tool {args.command}: {e}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
