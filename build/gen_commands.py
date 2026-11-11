"""Generate the InsideDev command, config key and MCP tool reference (Markdown) from the C# source tree.

Usage: python -I gen_commands.py <tree root> <output file>
"""
from __future__ import annotations

import copy
import itertools
import json
import re
import sys
from bisect import bisect_left
from dataclasses import dataclass, field
from pathlib import Path

sys.setrecursionlimit(20000)

ESC = {"n": "\n", "t": "\t", "r": "\r", "0": "\0", "\\": "\\", '"': '"', "'": "'", "a": "\a", "b": "\b", "f": "\f", "v": "\v"}
OPS3 = ("??=", "<<=", ">>=")
OPS2 = ("=>", "==", "!=", "<=", ">=", "&&", "||", "++", "--", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "??", "?.", "::", "<<")
PRIM = {"double", "float", "int", "long", "string", "object", "bool", "byte", "short", "uint", "ulong", "decimal", "char", "sbyte", "ushort"}
DECL_KW = {"class", "struct", "interface", "enum"}
BIN = {"??": 1, "||": 2, "&&": 3, "|": 4, "^": 5, "&": 6, "==": 7, "!=": 7, "<": 8, ">": 8, "<=": 8, ">=": 8,
       "is": 8, "as": 8, "<<": 9, "+": 10, "-": 10, "*": 11, "/": 11, "%": 11}
NUM_RE = re.compile(r"0[xX][0-9a-fA-F_]+[uUlL]*|(?:\d[\d_]*)?\.?\d[\d_]*(?:[eE][+-]?\d+)?[fFdDmMuUlL]*")
ID_RE = re.compile(r"@?[^\W\d]\w*")
BUILTIN_STATIC = {("int", "MaxValue"): 2147483647, ("int", "MinValue"): -2147483648, ("long", "MaxValue"): 9223372036854775807,
                  ("double", "MaxValue"): 1.7976931348623157e308, ("float", "MaxValue"): 3.4028234663852886e38,
                  ("string", "Empty"): "", ("String", "Empty"): ""}


@dataclass
class Tok:
    k: str
    v: object
    line: int
    raw: str


def read_escape(src: str, i: int) -> tuple[str, int]:
    n = src[i + 1]
    if n == "u":
        return chr(int(src[i + 2:i + 6], 16)), i + 6
    if n == "x":
        m = re.match(r"[0-9A-Fa-f]{1,4}", src[i + 2:])
        return chr(int(m.group(), 16)), i + 2 + len(m.group())
    return ESC.get(n, n), i + 2


def read_string(src: str, i: int, verbatim: bool, interp: bool) -> tuple[str, int]:
    out = []
    while i < len(src):
        c = src[i]
        if verbatim and c == '"':
            if src[i + 1:i + 2] == '"':
                out.append('"')
                i += 2
                continue
            return "".join(out), i + 1
        if not verbatim and c == "\\":
            v, i = read_escape(src, i)
            out.append(v)
            continue
        if not verbatim and c == '"':
            return "".join(out), i + 1
        if interp and c == "{":
            if src[i + 1:i + 2] == "{":
                out.append("{")
                i += 2
                continue
            depth, j = 0, i
            while j < len(src):
                ch = src[j]
                if ch == '"':
                    _, j = read_string(src, j + 1, False, False)
                    continue
                if ch == "{":
                    depth += 1
                elif ch == "}":
                    depth -= 1
                    if depth == 0:
                        break
                j += 1
            out.append(src[i:j + 1])
            i = j + 1
            continue
        if interp and c == "}" and src[i + 1:i + 2] == "}":
            out.append("}")
            i += 2
            continue
        out.append(c)
        i += 1
    return "".join(out), i


def parse_number(s: str) -> int | float:
    t = s.replace("_", "")
    if t[:2] in ("0x", "0X"):
        return int(t[2:].rstrip("uUlL"), 16)
    body = t.rstrip("fFdDmMuUlL")
    suffix = t[len(body):]
    if "." in body or "e" in body or "E" in body or any(c in suffix for c in "fFdDmM"):
        return float(body)
    return int(body)


def tokenize(src: str) -> list[Tok]:
    nl = [m.start() for m in re.finditer("\n", src)]
    toks: list[Tok] = []
    i, n, bol = 0, len(src), True
    while i < n:
        c = src[i]
        if c == "\n":
            i += 1
            bol = True
            continue
        if c in " \t\r\f\v﻿":
            i += 1
            continue
        if c == "#" and bol:
            j = src.find("\n", i)
            i = n if j < 0 else j
            continue
        bol = False
        if src.startswith("//", i):
            j = src.find("\n", i)
            i = n if j < 0 else j
            continue
        if src.startswith("/*", i):
            j = src.find("*/", i + 2)
            i = n if j < 0 else j + 2
            continue
        start = i
        line = bisect_left(nl, i) + 1
        kind = None
        if src.startswith(('$@"', '@$"'), i):
            kind, verb, interp, i = "istr", True, True, i + 3
        elif src.startswith('$"', i):
            kind, verb, interp, i = "istr", False, True, i + 2
        elif src.startswith('@"', i):
            kind, verb, interp, i = "str", True, False, i + 2
        elif c == '"':
            kind, verb, interp, i = "str", False, False, i + 1
        if kind:
            val, i = read_string(src, i, verb, interp)
            toks.append(Tok(kind, val, line, src[start:i]))
            continue
        if c == "'":
            j = i + 1
            if src[j] == "\\":
                val, j = read_escape(src, j)
            else:
                val, j = src[j], j + 1
            i = j + 1
            toks.append(Tok("chr", val, line, src[start:i]))
            continue
        if c.isdigit() or (c == "." and i + 1 < n and src[i + 1].isdigit()):
            m = NUM_RE.match(src, i)
            i = m.end()
            toks.append(Tok("num", parse_number(m.group()), line, m.group()))
            continue
        m = ID_RE.match(src, i)
        if m:
            i = m.end()
            toks.append(Tok("id", m.group().lstrip("@"), line, m.group()))
            continue
        for ops in (OPS3, OPS2):
            hit = next((o for o in ops if src.startswith(o, i)), None)
            if hit:
                break
        hit = hit or c
        i += len(hit)
        toks.append(Tok("op", hit, line, hit))
    return toks


def match_brackets(toks: list[Tok]) -> dict[int, int]:
    pairs = {"(": ")", "[": "]", "{": "}"}
    closers = {v: k for k, v in pairs.items()}
    stack: list[int] = []
    out: dict[int, int] = {}
    for i, t in enumerate(toks):
        if t.k != "op":
            continue
        if t.v in pairs:
            stack.append(i)
        elif t.v in closers:
            while stack and toks[stack[-1]].v != closers[t.v]:
                stack.pop()
            if stack:
                j = stack.pop()
                out[j] = i
                out[i] = j
    return out


class SrcFile:
    def __init__(self, path: Path, rel: str):
        self.path = path
        self.rel = rel
        self.text = path.read_text(encoding="utf-8-sig", errors="replace")
        self.toks = tokenize(self.text)
        self.match = match_brackets(self.toks)
        self.methods: list[tuple[int, int, MethodInfo]] = []
        self.classes: list[tuple[int, int, ClassInfo]] = []

    def is_op(self, i: int, v: str) -> bool:
        return 0 <= i < len(self.toks) and self.toks[i].k == "op" and self.toks[i].v == v

    def method_at(self, p: int) -> MethodInfo | None:
        best = None
        for b0, b1, m in self.methods:
            if b0 <= p < b1 and (best is None or b0 > best.b0):
                best = m
        return best

    def class_at(self, p: int) -> ClassInfo | None:
        best = None
        for b0, b1, c in self.classes:
            if b0 <= p < b1 and (best is None or b0 > best.b0):
                best = c
        return best

    def stmt_end(self, i: int, end: int | None = None) -> int:
        end = len(self.toks) if end is None else end
        while i < end:
            t = self.toks[i]
            if t.k == "op":
                if t.v in ("(", "[", "{") and i in self.match:
                    i = self.match[i] + 1
                    continue
                if t.v in (";", ")", "]", "}"):
                    return i
            i += 1
        return end


@dataclass(eq=False)
class ClassInfo:
    name: str
    kind: str
    file: SrcFile
    outer: ClassInfo | None
    bases: list
    b0: int
    b1: int
    fields: dict = field(default_factory=dict)
    methods: dict = field(default_factory=dict)
    enum: dict = field(default_factory=dict)


@dataclass(eq=False)
class MethodInfo:
    name: str
    cls: ClassInfo | None
    file: SrcFile
    params: list
    b0: int
    b1: int
    expr: bool
    prop: bool = False


class Index:
    def __init__(self, root: Path):
        self.root = root
        self.files: list[SrcFile] = []
        self.classes: dict[str, list[ClassInfo]] = {}
        for p in sorted((root / "src").rglob("*.cs")):
            f = SrcFile(p, p.relative_to(root).as_posix())
            self.files.append(f)
            self.scan(f, 0, len(f.toks), None)

    def add_method(self, f: SrcFile, cls: ClassInfo | None, m: MethodInfo) -> None:
        f.methods.append((m.b0, m.b1, m))
        if cls is not None:
            cls.methods.setdefault(m.name, []).append(m)

    def params(self, f: SrcFile, a: int, b: int) -> list:
        out, cur, i = [], [], a
        while i <= b:
            t = f.toks[i] if i < b else None
            if t is None or (t.k == "op" and t.v == ","):
                if cur:
                    names = [x for x in cur if f.toks[x].k == "id"]
                    eq = next((x for x in cur if f.is_op(x, "=")), None)
                    name_idx = [x for x in names if eq is None or x < eq]
                    name = f.toks[name_idx[-1]].v if name_idx else "?"
                    dflt = (eq + 1, cur[-1] + 1) if eq is not None else None
                    is_params = any(f.toks[x].v == "params" for x in names)
                    out.append((name, dflt, is_params))
                cur = []
                i += 1
                continue
            if t.k == "op" and t.v in ("(", "[", "{", "<") and i in f.match:
                cur.extend(range(i, f.match[i] + 1))
                i = f.match[i] + 1
                continue
            cur.append(i)
            i += 1
        return out

    def field_end(self, f: SrcFile, k: int, end: int) -> int:
        t = f.toks
        while k < end:
            tk = t[k]
            if tk.k == "op":
                if tk.v in ("(", "[", "{") and k in f.match:
                    k = f.match[k] + 1
                    continue
                if tk.v == ";":
                    return k
                if tk.v == "," and k + 2 < end and t[k + 1].k == "id" and f.is_op(k + 2, "="):
                    return k
            k += 1
        return end

    def parse_enum(self, ci: ClassInfo, f: SrcFile, a: int, b: int) -> None:
        i, nxt = a, 0
        while i < b:
            if f.toks[i].k != "id":
                i += 1
                continue
            name = f.toks[i].v
            j = i + 1
            if f.is_op(j, "="):
                e = j + 1
                while e < b and not f.is_op(e, ","):
                    e += 1
                vals = [f.toks[x].v for x in range(j + 1, e) if f.toks[x].k == "num"]
                if len(vals) == 1 and e == j + 2:
                    nxt = vals[0]
                else:
                    nxt = sum(ci.enum.get(f.toks[x].v, 0) for x in range(j + 1, e) if f.toks[x].k == "id") or nxt
                ci.enum[name] = nxt
                i = e + 1
            else:
                ci.enum[name] = nxt
                i = j + 1
            nxt += 1

    def scan(self, f: SrcFile, i: int, end: int, cls: ClassInfo | None) -> None:
        t = f.toks
        while i < end:
            tk = t[i]
            if tk.k == "op" and tk.v in (";", "}"):
                i += 1
                continue
            if tk.k == "op" and tk.v == "[" and i in f.match:
                i = f.match[i] + 1
                continue
            j, stop = i, None
            while j < end:
                x = t[j]
                if x.k == "op" and x.v in (";", "{", "=", "=>", "("):
                    stop = x.v
                    break
                if x.k == "op" and x.v == "[" and j in f.match:
                    j = f.match[j] + 1
                    continue
                j += 1
            if stop is None:
                return
            hv = [x.v for x in t[i:j] if x.k == "id"]
            kw = next((x for x in hv if x in DECL_KW), None)
            if "namespace" in hv and stop == "{":
                close = f.match.get(j, end)
                self.scan(f, j + 1, close, None)
                i = close + 1
                continue
            if kw and stop in ("{", "("):
                k = j
                while k < end and not (t[k].k == "op" and t[k].v in ("{", ";")):
                    k = f.match[k] + 1 if (t[k].k == "op" and t[k].v == "(" and k in f.match) else k + 1
                if k >= end or t[k].v == ";":
                    i = k + 1
                    continue
                close = f.match.get(k, end)
                pos = hv.index(kw)
                name = hv[pos + 1] if pos + 1 < len(hv) else "?"
                head = [x for x in t[i:k]]
                colon = next((n for n, x in enumerate(head) if x.k == "op" and x.v == ":"), None)
                bases = [x.v for x in head[colon + 1:] if x.k == "id"] if colon is not None else []
                ci = ClassInfo(name, kw, f, cls, bases, k + 1, close)
                self.classes.setdefault(name, []).append(ci)
                f.classes.append((k + 1, close, ci))
                if kw == "enum":
                    self.parse_enum(ci, f, k + 1, close)
                else:
                    self.scan(f, k + 1, close, ci)
                i = close + 1
                continue
            mods = {x for x in hv if x in ("static", "const", "readonly")}
            if stop == "(":
                name = t[j - 1].v if j > i else "?"
                pclose = f.match.get(j)
                if pclose is None:
                    i = j + 1
                    continue
                params = self.params(f, j + 1, pclose)
                k = pclose + 1
                while k < end and not (t[k].k == "op" and t[k].v in ("{", "=>", ";")):
                    k = f.match[k] + 1 if (t[k].k == "op" and t[k].v in ("(", "[") and k in f.match) else k + 1
                if k >= end:
                    i = k
                    continue
                if t[k].v == "{":
                    close = f.match.get(k, end)
                    self.add_method(f, cls, MethodInfo(name, cls, f, params, k + 1, close, False))
                    i = close + 1
                elif t[k].v == "=>":
                    e = f.stmt_end(k + 1, end)
                    self.add_method(f, cls, MethodInfo(name, cls, f, params, k + 1, e, True))
                    i = e + 1
                else:
                    i = k + 1
                continue
            if stop == "=":
                name = t[j - 1].v
                k = j + 1
                while True:
                    e = self.field_end(f, k, end)
                    if cls is not None:
                        cls.fields[name] = (k, e, mods)
                    if e < end and f.is_op(e, ","):
                        name = t[e + 1].v
                        k = e + 3
                        continue
                    i = e + 1
                    break
                continue
            if stop == "=>":
                e = f.stmt_end(j + 1, end)
                self.add_method(f, cls, MethodInfo(t[j - 1].v, cls, f, [], j + 1, e, True, True))
                i = e + 1
                continue
            if stop == "{":
                close = f.match.get(j, end)
                g = next((x for x in range(j + 1, close) if t[x].k == "id" and t[x].v == "get"), None)
                if g is not None and g + 1 < close:
                    if f.is_op(g + 1, "{") and (g + 1) in f.match:
                        self.add_method(f, cls, MethodInfo(t[j - 1].v, cls, f, [], g + 2, f.match[g + 1], False, True))
                    elif f.is_op(g + 1, "=>"):
                        self.add_method(f, cls, MethodInfo(t[j - 1].v, cls, f, [], g + 2, f.stmt_end(g + 2, close), True, True))
                i = close + 1
                if f.is_op(i, "="):
                    i = f.stmt_end(i + 1, end) + 1
                continue
            i = j + 1


class PErr(Exception):
    pass


class Parser:
    def __init__(self, f: SrcFile, i: int, end: int):
        self.f, self.t, self.i, self.end = f, f.toks, i, end

    def cur(self) -> Tok | None:
        return self.t[self.i] if self.i < self.end else None

    def at(self, v: str, off: int = 0, k: str = "op") -> bool:
        j = self.i + off
        return j < self.end and self.t[j].k == k and self.t[j].v == v

    def take(self, v: str) -> None:
        if not self.at(v):
            raise PErr(f"expected {v} at line {self.t[min(self.i, len(self.t) - 1)].line}")
        self.i += 1

    def skip_to(self, j: int) -> None:
        self.i = j

    def is_lambda(self) -> bool:
        c = self.cur()
        if c is None:
            return False
        if c.k == "id" and self.at("=>", 1):
            return True
        if c.k == "op" and c.v == "(" and self.i in self.f.match:
            return self.f.is_op(self.f.match[self.i] + 1, "=>")
        return False

    def lambda_(self) -> tuple:
        idx = self.i
        if self.cur().k == "id":
            self.i += 1
        else:
            self.i = self.f.match[self.i] + 1
        self.take("=>")
        if self.at("{"):
            self.i = self.f.match[self.i] + 1
        else:
            self.expr()
        return ("unknown", "lambda", idx)

    def expr(self) -> tuple:
        if self.is_lambda():
            return self.lambda_()
        c = self.binary(1)
        if self.at("?"):
            self.i += 1
            a = self.expr()
            self.take(":")
            b = self.expr()
            return ("cond", c, a, b)
        return c

    def binary(self, minp: int) -> tuple:
        left = self.unary()
        while True:
            tk = self.cur()
            if tk is None or tk.k not in ("op", "id") or not isinstance(tk.v, str) or tk.v not in BIN:
                break
            if tk.k == "id" and tk.v not in ("is", "as"):
                break
            if tk.k == "op" and tk.v in ("is", "as"):
                break
            p = BIN[tk.v]
            if p < minp:
                break
            self.i += 1
            if tk.v in ("is", "as"):
                self.type_name()
                left = ("unknown", tk.v, self.i)
                continue
            right = self.binary(p + 1)
            left = ("bin", tk.v, left, right)
        return left

    def is_cast(self) -> bool:
        j = self.i
        close = self.f.match.get(j)
        if close is None or close >= self.end - 1:
            return False
        inner = self.t[j + 1:close]
        if not inner:
            return False
        nxt = self.t[close + 1]
        if len(inner) == 1 and inner[0].k == "id" and inner[0].v in PRIM:
            return True
        if not all(x.k == "id" or (x.k == "op" and x.v == ".") for x in inner):
            return False
        if nxt.k in ("num", "str", "chr", "istr"):
            return True
        if nxt.k == "id" and nxt.v not in ("is", "as"):
            return True
        return nxt.k == "op" and nxt.v == "("

    def unary(self) -> tuple:
        tk = self.cur()
        if tk is None:
            raise PErr("end of expression")
        if tk.k == "op" and tk.v in ("-", "!", "+", "~"):
            self.i += 1
            return ("un", tk.v, self.unary())
        if tk.k == "op" and tk.v in ("++", "--"):
            self.i += 1
            return self.unary()
        if tk.k == "op" and tk.v == "(" and self.is_cast():
            self.i = self.f.match[self.i] + 1
            return self.unary()
        return self.postfix(self.primary())

    def generic_ahead(self) -> int | None:
        j, depth = self.i, 0
        while j < self.end:
            x = self.t[j]
            if x.k == "op" and x.v == "<":
                depth += 1
            elif x.k == "op" and x.v == ">":
                depth -= 1
                if depth == 0:
                    return j + 1 if self.f.is_op(j + 1, "(") else None
            elif not (x.k == "id" or (x.k == "op" and x.v in (".", ",", "[", "]", "?"))):
                return None
            j += 1
        return None

    def type_name(self) -> str:
        parts = []
        depth = 0
        while self.i < self.end:
            x = self.t[self.i]
            if x.k == "id" or (x.k == "op" and x.v in (".", "?")) or (depth > 0 and x.k == "op" and x.v in (",", "[", "]")):
                parts.append(x.v)
                self.i += 1
                continue
            if x.k == "op" and x.v == "<":
                depth += 1
                parts.append("<")
                self.i += 1
                continue
            if x.k == "op" and x.v == ">" and depth > 0:
                depth -= 1
                parts.append(">")
                self.i += 1
                continue
            break
        return "".join(parts)

    def primary(self) -> tuple:
        tk = self.cur()
        idx = self.i
        if tk.k in ("num", "str", "chr", "istr"):
            self.i += 1
            return ("lit", tk.v)
        if tk.k == "op":
            if tk.v == "(":
                self.i += 1
                e = self.expr()
                self.take(")")
                return e
            if tk.v == "{":
                return ("arr", self.init_list())
            raise PErr(f"unexpected {tk.v} at line {tk.line}")
        v = tk.v
        if v == "true":
            self.i += 1
            return ("lit", True)
        if v == "false":
            self.i += 1
            return ("lit", False)
        if v == "null":
            self.i += 1
            return ("lit", None)
        if v == "new":
            self.i += 1
            return self.new_()
        if v in ("typeof", "default", "sizeof", "checked", "unchecked") and self.at("(", 1):
            self.i = self.f.match[self.i + 1] + 1
            return ("unknown", v, idx)
        if v == "nameof" and self.at("(", 1):
            close = self.f.match[self.i + 1]
            name = self.t[close - 1].v
            self.i = close + 1
            return ("lit", name)
        self.i += 1
        if self.at("<"):
            g = self.generic_ahead()
            if g:
                self.i = g - 1
        return ("name", v, idx)

    def postfix(self, e: tuple) -> tuple:
        while True:
            tk = self.cur()
            if tk is None or tk.k != "op":
                return e
            if tk.v in (".", "?."):
                self.i += 1
                nm = self.cur()
                if nm is None or nm.k != "id":
                    raise PErr("member name expected")
                self.i += 1
                if self.at("<"):
                    g = self.generic_ahead()
                    if g:
                        self.i = g - 1
                e = ("member", e, nm.v)
            elif tk.v == "(":
                idx = self.i
                e = ("call", e, self.args("(", ")"), idx)
            elif tk.v == "[":
                e = ("index", e, self.args("[", "]"))
            elif tk.v in ("++", "--"):
                self.i += 1
            else:
                return e

    def args(self, o: str, c: str) -> list:
        self.take(o)
        out = []
        while not self.at(c):
            cur = self.cur()
            if cur is None:
                raise PErr("unclosed arguments")
            name = None
            if cur.k == "id" and self.at(":", 1):
                name = cur.v
                self.i += 2
            cur = self.cur()
            if cur.k == "id" and cur.v == "out":
                j = self.i
                while j < self.end and not (self.t[j].k == "op" and self.t[j].v in (",", c)):
                    j = self.f.match[j] + 1 if (self.t[j].k == "op" and self.t[j].v in ("(", "[", "{") and j in self.f.match) else j + 1
                self.i = j
                out.append((name, ("unknown", "out", j)))
            else:
                while self.cur().k == "id" and self.cur().v in ("ref", "in", "params"):
                    self.i += 1
                out.append((name, self.expr()))
            if self.at(","):
                self.i += 1
            elif not self.at(c):
                raise PErr(f"expected , or {c} at line {self.cur().line if self.cur() else '?'}")
        self.i += 1
        return out

    def init_list(self) -> list:
        self.take("{")
        out = []
        while not self.at("}"):
            if self.cur() is None:
                raise PErr("unclosed initializer")
            if self.at("{"):
                out.append(("arr", self.init_list()))
            else:
                out.append(self.expr())
            if self.at(","):
                self.i += 1
            elif not self.at("}"):
                raise PErr("expected , or }")
        self.i += 1
        return out

    def obj_init(self) -> list:
        self.take("{")
        out = []
        while not self.at("}"):
            nm = self.cur()
            if nm is None or nm.k != "id":
                raise PErr("member initializer expected")
            self.i += 1
            self.take("=")
            val = ("arr", self.init_list()) if self.at("{") else self.expr()
            out.append((nm.v, val))
            if self.at(","):
                self.i += 1
            elif not self.at("}"):
                raise PErr("expected , or }")
        self.i += 1
        return out

    def new_(self) -> tuple:
        if self.at("["):
            self.i = self.f.match[self.i] + 1
            return ("arr", self.init_list())
        if self.at("{"):
            return ("new", "", None, ("obj", self.obj_init()))
        tname = self.type_name()
        if self.at("["):
            while self.at("["):
                self.i = self.f.match[self.i] + 1
            if self.at("{"):
                return ("arr", self.init_list())
            return ("arr", [])
        args = self.args("(", ")") if self.at("(") else None
        init = None
        if self.at("{"):
            c1 = self.t[self.i + 1] if self.i + 1 < self.end else None
            if c1 is not None and c1.k == "id" and self.at("=", 2):
                init = ("obj", self.obj_init())
            else:
                init = ("coll", self.init_list())
        return ("new", tname, args, init)


class Unknown:
    def __init__(self, text: str):
        self.text = text

    def __repr__(self) -> str:
        return f"<{self.text}>"


class Namespace(Unknown):
    pass


@dataclass
class ClassRef:
    name: str


@dataclass
class EnumVal:
    enum: str
    name: str
    value: int


class Inst:
    def __init__(self, type_: str):
        self.type = type_
        self.f: dict = {}


class SObj:
    def __init__(self):
        self.props: dict = {}
        self.req: list = []
        self.open = False

    def build(self) -> dict:
        o = {"type": "object", "properties": self.props}
        if self.req:
            o["required"] = list(self.req)
        o["additionalProperties"] = self.open
        return o

    def build_nested(self) -> dict:
        return {"type": "object", "properties": self.props}


@dataclass
class Ctx:
    f: SrcFile
    cls: ClassInfo | None
    method: MethodInfo | None
    pos: int
    binds: dict
    depth: int = 0

    def sub(self, **kw) -> Ctx:
        d = dict(f=self.f, cls=self.cls, method=self.method, pos=self.pos, binds=self.binds, depth=self.depth + 1)
        d.update(kw)
        return Ctx(**d)


def render(n: tuple) -> str:
    k = n[0]
    if k == "lit":
        return repr(n[1])
    if k == "name":
        return n[1]
    if k == "member":
        return render(n[1]) + "." + n[2]
    if k == "call":
        return render(n[1]) + "(" + ", ".join(render(a[1]) for a in n[2]) + ")"
    if k == "unknown":
        return n[1]
    if k == "bin":
        return render(n[2]) + " " + n[1] + " " + render(n[3])
    return k


def fmt_num(v) -> str:
    if isinstance(v, bool):
        return "true" if v else "false"
    if isinstance(v, int):
        return str(v)
    if isinstance(v, float):
        if v.is_integer() and abs(v) < 1e15:
            return str(int(v))
        r = repr(v).replace("e", "E")
        return re.sub(r"E(\d)", r"E+\1", r)
    return str(v)


def cs_str(v) -> str:
    if isinstance(v, str):
        return v
    if isinstance(v, bool):
        return "True" if v else "False"
    if isinstance(v, (int, float)):
        return fmt_num(v)
    if v is None:
        return ""
    if isinstance(v, EnumVal):
        return v.name
    if isinstance(v, Unknown):
        return f"<{v.text}>"
    if isinstance(v, ClassRef):
        return f"<{v.name}>"
    return f"<{type(v).__name__}>"


def is_num(v) -> bool:
    return isinstance(v, (int, float)) and not isinstance(v, bool)


def flat(vals: list) -> list:
    if len(vals) == 1 and isinstance(vals[0], list):
        return list(vals[0])
    return list(vals)


NOTFOUND = object()


class Evaluator:
    def __init__(self, idx: Index):
        self.idx = idx
        self.field_cache: dict = {}
        self.busy: set = set()
        self.active: set = set()
        self.misses: list = []

    def miss(self, text: str, cx: Ctx | None) -> None:
        where = ""
        if cx is not None and cx.pos < len(cx.f.toks):
            where = f"{cx.f.rel}:{cx.f.toks[cx.pos].line}"
        self.misses.append((text, where))

    def parse_eval(self, f: SrcFile, a: int, b: int, cx: Ctx):
        try:
            node = Parser(f, a, b).expr()
        except (PErr, IndexError, KeyError) as e:
            self.miss(f"parse: {e}", cx)
            return Unknown(" ".join(str(t.raw) for t in f.toks[a:min(b, a + 8)]))
        return self.ev(node, cx)

    def ev(self, n: tuple, cx: Ctx):
        if cx.depth > 90:
            return Unknown("too deep")
        k = n[0]
        if k == "lit":
            return n[1]
        if k == "name":
            return self.resolve(n[1], cx, n[2])
        if k == "member":
            return self.member(self.ev(n[1], cx), n[2], cx)
        if k == "call":
            return self.call(n, cx)
        if k == "index":
            base = self.ev(n[1], cx)
            key = self.ev(n[2][0][1], cx) if n[2] else None
            if isinstance(base, dict):
                return base.get(key, Unknown(render(n[1]) + "[...]"))
            if isinstance(base, (list, str)) and isinstance(key, int) and 0 <= key < len(base):
                return base[key]
            return Unknown(render(n[1]) + "[...]")
        if k == "arr":
            return [self.ev(x, cx) for x in n[1]]
        if k == "new":
            return self.new(n, cx)
        if k == "bin":
            return self.binop(n[1], n[2], n[3], cx)
        if k == "un":
            v = self.ev(n[2], cx)
            if n[1] == "-" and is_num(v):
                return -v
            if n[1] == "+" and is_num(v):
                return v
            if n[1] == "!" and isinstance(v, bool):
                return not v
            return Unknown(n[1] + render(n[2]))
        if k == "cond":
            c = self.ev(n[1], cx)
            if isinstance(c, bool):
                return self.ev(n[2] if c else n[3], cx)
            a, b = self.ev(n[2], cx), self.ev(n[3], cx)
            if isinstance(a, str) and isinstance(b, str):
                if not a or not b:
                    return a or b
                return a + "|" + b
            return Unknown("conditional")
        if k == "unknown":
            return Unknown(n[1])
        return Unknown(k)

    def binop(self, op: str, ln: tuple, rn: tuple, cx: Ctx):
        lv = self.ev(ln, cx)
        if op == "&&" and lv is False:
            return False
        if op == "||" and lv is True:
            return True
        rv = self.ev(rn, cx)
        if op == "+" and (isinstance(lv, str) or isinstance(rv, str)):
            for v, node in ((lv, ln), (rv, rn)):
                if isinstance(v, Unknown):
                    self.miss(f"placeholder <{v.text}>", cx)
            return cs_str(lv) + cs_str(rv)
        if is_num(lv) and is_num(rv):
            if op == "+":
                return lv + rv
            if op == "-":
                return lv - rv
            if op == "*":
                return lv * rv
            if op == "/":
                if rv == 0:
                    return Unknown("division by zero")
                if isinstance(lv, int) and isinstance(rv, int):
                    q = abs(lv) // abs(rv)
                    return q if (lv >= 0) == (rv >= 0) else -q
                return lv / rv
            if op == "%" and rv != 0:
                return lv % rv
            if op in ("<", ">", "<=", ">="):
                return {"<": lv < rv, ">": lv > rv, "<=": lv <= rv, ">=": lv >= rv}[op]
        if isinstance(lv, EnumVal) and isinstance(rv, EnumVal) and op in ("|", "&"):
            val = lv.value | rv.value if op == "|" else lv.value & rv.value
            return EnumVal(lv.enum, f"{lv.name}{op}{rv.name}", val)
        if op in ("==", "!=") and not isinstance(lv, Unknown) and not isinstance(rv, Unknown):
            eq = (lv.value == rv.value) if isinstance(lv, EnumVal) and isinstance(rv, EnumVal) else lv == rv
            return eq if op == "==" else not eq
        if op == "&&" and isinstance(lv, bool) and isinstance(rv, bool):
            return lv and rv
        if op == "||" and isinstance(lv, bool) and isinstance(rv, bool):
            return lv or rv
        if op == "??":
            return rv if lv is None else lv
        return Unknown(f"{render(ln)} {op} {render(rn)}")

    def classes(self, name: str) -> list[ClassInfo]:
        return self.idx.classes.get(name, [])

    def eval_field(self, ci: ClassInfo, name: str, depth: int):
        key = (id(ci), name)
        if key in self.field_cache:
            return copy.deepcopy(self.field_cache[key])
        if key in self.busy:
            return Unknown(f"{ci.name}.{name}")
        self.busy.add(key)
        a, b, mods = ci.fields[name]
        if "static" in mods and not ({"const", "readonly"} & mods):
            return Unknown(name)
        cx = Ctx(ci.file, ci, None, a, {}, depth + 1)
        try:
            v = self.parse_eval(ci.file, a, b, cx)
        finally:
            self.busy.discard(key)
        self.field_cache[key] = v
        return copy.deepcopy(v)

    def foreach_vars(self, m: MethodInfo) -> list:
        f, t, out = m.file, m.file.toks, []
        for j in range(m.b0, m.b1):
            if t[j].k == "id" and t[j].v == "foreach" and f.is_op(j + 1, "(") and (j + 1) in f.match:
                close = f.match[j + 1]
                inn = next((x for x in range(j + 2, close) if t[x].k == "id" and t[x].v == "in"), None)
                if inn is None:
                    continue
                var = t[inn - 1].v
                b = close + 1
                if f.is_op(b, "{") and b in f.match:
                    end = f.match[b]
                else:
                    end = b
                    while end < m.b1 and not f.is_op(end, ";"):
                        if f.is_op(end, "{") and end in f.match:
                            end = f.match[end]
                            break
                        end = f.match[end] + 1 if (t[end].k == "op" and t[end].v in ("(", "[") and end in f.match) else end + 1
                out.append((var, inn + 1, close, b, end))
        return out

    def local(self, ident: str, cx: Ctx, idx: int):
        m = cx.method
        f, t = m.file, m.file.toks
        best = None
        for j in range(m.b0, min(idx, m.b1)):
            if t[j].k != "id" or t[j].v != ident or not f.is_op(j + 1, "="):
                continue
            p = t[j - 1]
            if p.k == "op" and p.v in (".", ",", "(", "?."):
                continue
            if p.k == "op" and p.v == "{" and j - 2 >= 0 and (t[j - 2].k == "id" or f.is_op(j - 2, ">") or f.is_op(j - 2, ")")):
                if f.is_op(j - 2, ")") and (j - 2) in f.match and t[f.match[j - 2] - 1].k == "id" and t[f.match[j - 2] - 1].v in ("if", "while", "for", "foreach", "using", "lock", "switch"):
                    pass
                else:
                    continue
            best = j
        if best is not None:
            semi = f.stmt_end(best + 2, m.b1)
            val = self.parse_eval(f, best + 2, semi, cx.sub(pos=best))
            val = self.mutate(val, ident, cx, semi + 1, idx)
            return Unknown(ident) if isinstance(val, Unknown) else val
        for name, dflt, _ in m.params:
            if name == ident:
                return Unknown(ident)
        for var, a, b, s, e in self.foreach_vars(m):
            if var == ident and s <= idx <= e:
                return Unknown(ident)
        return NOTFOUND

    def mutate(self, val, ident: str, cx: Ctx, a: int, b: int):
        m = cx.method
        f, t = m.file, m.file.toks
        j = a
        while j < min(b, m.b1):
            if t[j].k == "id" and t[j].v == ident and t[j - 1].k == "op" and t[j - 1].v in (";", "{", "}"):
                semi = f.stmt_end(j, m.b1)
                bx = cx.sub(pos=j, binds={**cx.binds, ident: val})
                if f.is_op(j + 1, "[") and (j + 1) in f.match and f.is_op(f.match[j + 1] + 1, "="):
                    close = f.match[j + 1]
                    key = self.parse_eval(f, j + 2, close, bx)
                    v = self.parse_eval(f, close + 2, semi, bx)
                    if isinstance(val, dict):
                        val[key] = v
                elif f.is_op(j + 1, ".") and t[j + 2].k == "id" and f.is_op(j + 3, "="):
                    v = self.parse_eval(f, j + 4, semi, bx)
                    if isinstance(val, Inst):
                        val.f[t[j + 2].v] = v
                    elif isinstance(val, dict):
                        val[t[j + 2].v] = v
                elif f.is_op(j + 1, ".") and t[j + 2].k == "id" and f.is_op(j + 3, "("):
                    self.parse_eval(f, j, semi, bx)
                j = semi
            j += 1
        return val

    def resolve(self, ident: str, cx: Ctx, idx: int):
        if ident in cx.binds:
            return cx.binds[ident]
        if cx.method is not None and cx.method.file is cx.f:
            r = self.local(ident, cx, idx)
            if r is not NOTFOUND:
                return r
        c = cx.cls
        while c is not None:
            if ident in c.fields:
                if not ({"static", "const", "readonly"} & c.fields[ident][2]):
                    return Unknown(ident)
                return self.eval_field(c, ident, cx.depth)
            if ident in c.enum:
                return EnumVal(c.name, ident, c.enum[ident])
            if any(m.prop for m in c.methods.get(ident, [])):
                v = self.invoke(c, ident, [], cx)
                return Unknown(ident) if isinstance(v, Unknown) else v
            c = c.outer
        if ident in self.idx.classes:
            return ClassRef(ident)
        return Namespace(ident)

    def member(self, base, name: str, cx: Ctx):
        if isinstance(base, ClassRef):
            for ci in self.classes(base.name):
                if name in ci.fields:
                    return self.eval_field(ci, name, cx.depth)
                if name in ci.enum:
                    return EnumVal(ci.name, name, ci.enum[name])
                if any(m.prop for m in ci.methods.get(name, [])):
                    v = self.invoke(ci, name, [], cx)
                    return Unknown(f"{base.name}.{name}") if isinstance(v, Unknown) else v
            for ci in self.classes(name):
                if ci.outer is not None and ci.outer.name == base.name:
                    return ClassRef(name)
            return Unknown(f"{base.name}.{name}")
        if isinstance(base, Namespace):
            if (base.text, name) in BUILTIN_STATIC:
                return BUILTIN_STATIC[(base.text, name)]
            if name in self.idx.classes:
                return ClassRef(name)
            return Namespace(f"{base.text}.{name}")
        if isinstance(base, Inst):
            return base.f.get(name, Unknown(f"{base.type}.{name}"))
        if isinstance(base, dict):
            return base.get(name, Unknown(name))
        if isinstance(base, (str, list)) and name in ("Length", "Count"):
            return len(base)
        if isinstance(base, Unknown):
            return Unknown(f"{base.text}.{name}")
        return Unknown(name)

    def argvals(self, args: list, cx: Ctx) -> list:
        return [self.ev(a[1], cx) for a in args]

    def call(self, n: tuple, cx: Ctx):
        callee, args = n[1], n[2]
        if callee[0] == "member":
            base = self.ev(callee[1], cx)
            return self.call_member(base, callee[2], args, cx, n)
        if callee[0] == "name":
            name = callee[1]
            c = cx.cls
            while c is not None:
                if name in c.methods:
                    return self.invoke(c, name, args, cx)
                c = c.outer
            return Unknown(render(n))
        return Unknown(render(n))

    def call_member(self, base, mname: str, args: list, cx: Ctx, n: tuple):
        bname = base.name if isinstance(base, ClassRef) else base.text if isinstance(base, Namespace) else None
        if bname is not None:
            fn = self.static_builtin(bname, mname)
            if fn is not None:
                return fn(self.argvals(args, cx))
            if isinstance(base, ClassRef):
                for ci in self.classes(bname):
                    if mname in ci.methods:
                        return self.invoke(ci, mname, args, cx)
            return Unknown(render(n))
        if isinstance(base, SObj):
            return self.sobj_call(base, mname, self.argvals(args, cx))
        if isinstance(base, Inst) and base.type == "ToolDef":
            return self.tooldef_call(base, mname, self.argvals(args, cx))
        if isinstance(base, str):
            a = self.argvals(args, cx)
            if mname in ("ToUpperInvariant", "ToUpper"):
                return base.upper()
            if mname in ("ToLowerInvariant", "ToLower"):
                return base.lower()
            if mname == "Trim" and not a:
                return base.strip()
            if mname == "Replace" and len(a) == 2 and all(isinstance(x, str) for x in a):
                return base.replace(a[0], a[1])
            if mname == "ToString":
                return base
        if is_num(base) and mname == "ToString" and not args:
            return cs_str(base)
        if isinstance(base, list) and mname == "ToArray":
            return base
        return Unknown(render(n))

    def static_builtin(self, cls: str, m: str):
        if cls == "Schema":
            return {
                "Obj": lambda a: SObj(),
                "Str": lambda a: {"type": "string"},
                "Int": lambda a: {"type": "integer", "minimum": a[0], "maximum": a[1]},
                "IntAny": lambda a: {"type": "integer"},
                "Num": lambda a: {"type": "number", "minimum": a[0], "maximum": a[1]},
                "NumAny": lambda a: {"type": "number"},
                "Bool": lambda a: {"type": "boolean"},
                "Enum": lambda a: {"type": "string", "enum": flat(a)},
                "Arr": lambda a: self.arr_schema(a[0], a[1], a[2]),
                "Vec": lambda a: self.arr_schema({"type": "number", "minimum": a[1], "maximum": a[2]}, a[0], a[0]),
                "VecAny": lambda a: self.arr_schema({"type": "number"}, a[0], a[0]),
                "ObjOf": lambda a: a[0].build() if isinstance(a[0], SObj) else {"type": "object"},
                "ObjAny": lambda a: {"type": "object"},
                "OutOf": lambda a: a[0].build_nested() if isinstance(a[0], SObj) else {"type": "object"},
                "Described": lambda a: {**a[0], "description": a[1]} if isinstance(a[0], dict) else a[0],
            }.get(m)
        if cls in ("string", "String"):
            if m == "Join":
                return lambda a: cs_str(a[0]).join(cs_str(x) for x in (a[1] if len(a) == 2 and isinstance(a[1], list) else a[1:]))
            if m == "Concat":
                return lambda a: "".join(cs_str(x) for x in flat(a))
        if cls == "Fmt":
            if m == "F":
                return lambda a: self.fmt_f(a[0]) if len(a) == 1 and is_num(a[0]) else Unknown("Fmt.F")
            if m == "I":
                return lambda a: cs_str(a[0]) if is_num(a[0]) else Unknown("Fmt.I")
        if cls == "Math" and m in ("Max", "Min"):
            return lambda a: (max if m == "Max" else min)(a) if all(is_num(x) for x in a) else Unknown("Math." + m)
        return None

    @staticmethod
    def fmt_f(v) -> str:
        s = f"{v:.3f}".rstrip("0").rstrip(".")
        return "0" if s in ("-0", "") else s

    @staticmethod
    def arr_schema(items, mn, mx) -> dict:
        s = {"type": "array", "items": items if isinstance(items, dict) else {}}
        if is_num(mn) and mn > 0:
            s["minItems"] = mn
        if is_num(mx) and mx > 0:
            s["maxItems"] = mx
        return s

    def sobj_call(self, s: SObj, m: str, a: list):
        def prop(name, desc, sch):
            sch = sch if isinstance(sch, dict) else {}
            if desc is not None:
                sch["description"] = desc
            s.props[name] = sch
        if m == "Prop":
            prop(a[0], a[1], a[2])
        elif m == "Str":
            sch = {"type": "string"}
            if len(a) > 2 and a[2] is not None:
                sch["default"] = a[2]
            prop(a[0], a[1], sch)
        elif m == "StrMax":
            prop(a[0], a[1], {"type": "string", "maxLength": a[2]})
        elif m in ("Int", "Num"):
            sch = {"type": "integer" if m == "Int" else "number", "minimum": a[-2], "maximum": a[-1]}
            if len(a) == 5:
                sch["default"] = a[2]
            prop(a[0], a[1], sch)
        elif m == "Bool":
            sch = {"type": "boolean"}
            if len(a) == 3:
                sch["default"] = a[2]
            prop(a[0], a[1], sch)
        elif m == "Enum":
            sch = {"type": "string", "enum": flat(a[3:])}
            if a[2] is not None:
                sch["default"] = a[2]
            prop(a[0], a[1], sch)
        elif m == "Arr":
            prop(a[0], a[1], self.arr_schema(a[2], a[3], a[4]))
        elif m == "Vec":
            prop(a[0], a[1], self.arr_schema({"type": "number", "minimum": a[3], "maximum": a[4]}, a[2], a[2]))
        elif m == "Obj":
            prop(a[0], a[1], a[2].build() if isinstance(a[2], SObj) else {"type": "object"})
        elif m == "OutObj":
            prop(a[0], a[1], a[2].build_nested() if isinstance(a[2], SObj) else {"type": "object"})
        elif m == "Any":
            prop(a[0], a[1], {})
        elif m == "Required":
            for x in flat(a):
                if x not in s.req:
                    s.req.append(x)
        elif m == "Default":
            if isinstance(s.props.get(a[0]), dict):
                s.props[a[0]]["default"] = a[1]
        elif m == "Open":
            s.open = True
        elif m == "Build":
            return s.build()
        elif m in ("BuildNested", "BuildOutput"):
            return s.build_nested()
        else:
            return Unknown("SchemaObj." + m)
        return s

    @staticmethod
    def tooldef_call(t: Inst, m: str, a: list):
        if m == "ReadOnly":
            t.f.update(readOnly=True, idempotent=True, destructive=False)
        elif m == "Destructive":
            t.f["destructive"] = True
        elif m == "Idempotent":
            t.f["idempotent"] = True
        elif m == "Owner":
            t.f["owner"] = a[0] or ""
        elif m == "Timeout":
            t.f["timeoutMs"] = a[0]
        elif m in ("Meta", "AlwaysLoad", "RequiresUser", "MaxChars"):
            key, val = {"AlwaysLoad": ("anthropic/alwaysLoad", True), "RequiresUser": ("anthropic/requiresUserInteraction", True),
                        "MaxChars": ("anthropic/maxResultSizeChars", a[0] if a else None)}.get(m, (a[0], a[1]) if len(a) == 2 else (None, None))
            meta = t.f.get("meta")
            if not isinstance(meta, dict):
                meta = {}
            meta[key] = val
            t.f["meta"] = meta
        return t

    def invoke(self, ci: ClassInfo, name: str, args: list, cx: Ctx):
        cands = ci.methods.get(name, [])
        n = len(args)
        pick = None
        for m in cands:
            req = sum(1 for p in m.params if p[1] is None and not p[2])
            has_params = any(p[2] for p in m.params)
            if req <= n and (n <= len(m.params) or has_params):
                if pick is None or len(m.params) == n:
                    pick = m
        if pick is None or id(pick) in self.active:
            return Unknown(f"{ci.name}.{name}()")
        self.active.add(id(pick))
        try:
            return self.run_method(pick, ci, name, args, cx)
        finally:
            self.active.discard(id(pick))

    def run_method(self, pick: MethodInfo, ci: ClassInfo, name: str, args: list, cx: Ctx):
        vals = self.argvals(args, cx)
        binds: dict = {}
        pos_vals = [v for (nm, _), v in zip(args, vals) if nm is None]
        named = {nm: v for (nm, _), v in zip(args, vals) if nm is not None}
        for k, (pname, dflt, is_params) in enumerate(pick.params):
            if is_params:
                rest = pos_vals[k:]
                binds[pname] = rest[0] if len(rest) == 1 and isinstance(rest[0], list) else rest
            elif pname in named:
                binds[pname] = named[pname]
            elif k < len(pos_vals):
                binds[pname] = pos_vals[k]
            elif dflt is not None:
                binds[pname] = self.parse_eval(pick.file, dflt[0], dflt[1], Ctx(pick.file, pick.cls, None, dflt[0], {}, cx.depth + 1))
            else:
                binds[pname] = Unknown(pname)
        mcx = Ctx(pick.file, pick.cls, pick, pick.b0, binds, cx.depth + 1)
        if pick.expr:
            return self.parse_eval(pick.file, pick.b0, pick.b1, mcx)
        t = pick.file.toks
        rets = [j for j in range(pick.b0, pick.b1) if t[j].k == "id" and t[j].v == "return"]
        if len(rets) != 1:
            return Unknown(f"{ci.name}.{name}()")
        j = rets[0]
        semi = pick.file.stmt_end(j + 1, pick.b1)
        return self.parse_eval(pick.file, j + 1, semi, mcx.sub(pos=j, depth=cx.depth + 1))

    def new(self, n: tuple, cx: Ctx):
        tname, args, init = n[1], n[2], n[3]
        base = re.sub(r"<.*", "", tname).split(".")[-1]
        elems = []
        if init is not None and init[0] == "coll":
            elems = init[1]
        if base == "Obj" or base == "Dictionary":
            d = {}
            for e in elems:
                if e[0] == "arr" and len(e[1]) == 2:
                    d[self.ev(e[1][0], cx)] = self.ev(e[1][1], cx)
            return d
        if base in ("List", "HashSet", "Queue", "Stack") or tname.endswith("[]"):
            return [self.ev(e, cx) for e in elems]
        if base == "SchemaObj":
            return SObj()
        if base in self.idx.classes and base not in ("StringBuilder",):
            inst = Inst(base)
            for ci in self.classes(base):
                for fname, (_, _, mods) in ci.fields.items():
                    if "static" not in mods and "const" not in mods:
                        try:
                            inst.f[fname] = self.eval_field(ci, fname, cx.depth)
                        except RecursionError:
                            inst.f[fname] = Unknown(fname)
            if init is not None and init[0] == "obj":
                for name, val in init[1]:
                    inst.f[name] = self.ev(val, cx)
            return inst
        return Unknown("new " + tname)


def ctx_at(ev: Evaluator, f: SrcFile, p: int, binds: dict | None = None) -> Ctx:
    return Ctx(f, f.class_at(p), f.method_at(p), p, binds or {}, 0)


def call_args_at(f: SrcFile, open_idx: int) -> list | None:
    try:
        p = Parser(f, open_idx, len(f.toks))
        return p.args("(", ")")
    except (PErr, IndexError, KeyError):
        return None


def loops_at(ev: Evaluator, f: SrcFile, p: int) -> list:
    m = f.method_at(p)
    if m is None:
        return []
    return [lp for lp in ev.foreach_vars(m) if lp[3] <= p <= lp[4]]


def expand_loops(ev: Evaluator, f: SrcFile, p: int) -> list[dict]:
    loops = loops_at(ev, f, p)
    combos: list[dict] = [{}]
    for var, a, b, s, e in loops:
        nxt = []
        for binds in combos:
            coll = ev.parse_eval(f, a, b, ctx_at(ev, f, a, binds))
            if isinstance(coll, list):
                nxt.extend({**binds, var: v} for v in coll)
            else:
                nxt.append({**binds, var: Unknown(var)})
        combos = nxt
    return combos


def first_word(s: str) -> str:
    s = s.strip()
    if s.startswith("usage:"):
        s = s[6:].strip()
    w = s.split(" ", 1)[0].split("\n", 1)[0]
    return w.rstrip(":")


def esc_text(s) -> str:
    s = cs_str(s) if not isinstance(s, str) else s
    s = s.replace("\r", "").replace("\n", " ")
    parts = s.split("`")
    out = []
    for n, p in enumerate(parts):
        if n % 2 == 1 and n < len(parts) - 1:
            out.append("`" + p.replace("|", "\\|") + "`")
        else:
            seg = p if n % 2 == 0 else "`" + p
            for a, b in (("\\", "\\\\"), ("*", "\\*"), ("_", "\\_"), ("<", "&lt;"), ("|", "\\|"), ("[", "\\["), ("]", "\\]")):
                seg = seg.replace(a, b)
            out.append(seg)
    return "".join(out)


def code(s) -> str:
    s = cs_str(s) if not isinstance(s, str) else s
    s = s.replace("\r", "").replace("\n", " ").replace("|", "\\|")
    if "`" in s:
        return "`` " + s + " ``"
    return "`" + s + "`"


class Anchors:
    def __init__(self):
        self.seen: dict[str, int] = {}

    def make(self, title: str) -> str:
        a = re.sub(r"[^\w\- ]", "", title.lower()).replace(" ", "-")
        n = self.seen.get(a, 0)
        self.seen[a] = n + 1
        return a if n == 0 else f"{a}-{n}"


def extract_commands(idx: Index, ev: Evaluator) -> tuple[list[Inst], SrcFile]:
    ci = idx.classes["CoreBHelp"][0]
    rows = ev.eval_field(ci, "table", 0)
    return [r for r in rows if isinstance(r, Inst)], ci.file


def extract_config(idx: Index, ev: Evaluator) -> tuple[list[dict], int]:
    out, sites = [], 0
    for f in idx.files:
        t = f.toks
        for j in range(1, len(t) - 1):
            if t[j].k != "id" or t[j].v != "Describe" or not f.is_op(j + 1, "("):
                continue
            via = t[j - 1].k == "op" and t[j - 1].v == "." and t[j - 2].v == "EditorState"
            cls = f.class_at(j)
            local = not (t[j - 1].k == "op" and t[j - 1].v == ".") and cls is not None and cls.name == "EditorState" and t[j - 1].v not in ("void", "string")
            if not (via or local):
                continue
            args = call_args_at(f, j + 1)
            if args is None or len(args) != 4:
                continue
            sites += 1
            for binds in expand_loops(ev, f, j):
                cx = ctx_at(ev, f, j, binds)
                ev.misses = []
                vals = [ev.ev(a[1], cx) for a in args]
                out.append({"part": vals[0], "key": vals[1], "def": vals[2], "text": vals[3],
                            "src": f"{f.rel}:{t[j].line}", "misses": list(ev.misses), "loop": bool(binds)})
    return out, sites


def extract_tools(idx: Index, ev: Evaluator) -> list[dict]:
    found: dict[tuple, dict] = {}
    for f in idx.files:
        if not (f.rel.startswith("src/mcp/") or f.rel.startswith("src/play/")):
            continue
        t = f.toks
        cands = []
        for j in range(1, len(t) - 2):
            if t[j].k == "id" and t[j].v == "Add" and f.is_op(j - 1, ".") and f.is_op(j + 1, "("):
                args = call_args_at(f, j + 1)
                if not args:
                    continue
                close = f.match.get(j + 1, j + 1)
                src = " ".join(str(x.raw) for x in t[j + 2:close])
                if re.search(r"\b(Def|T)\s*\(|new\s+ToolDef", src) or args[0][1][0] == "name":
                    cands.append((j, args[0][1]))
            if t[j].k == "id" and t[j].v == "new" and t[j + 1].v == "ToolDef":
                try:
                    p = Parser(f, j, len(t))
                    node = p.postfix(p.primary())
                except (PErr, IndexError, KeyError):
                    continue
                cands.append((j, node))
        for j, node in cands:
            for binds in expand_loops(ev, f, j):
                ev.misses = []
                v = ev.ev(node, ctx_at(ev, f, j, binds))
                if isinstance(v, Inst) and v.type == "ToolDef" and isinstance(v.f.get("name"), str):
                    key = (v.f["name"], f.rel)
                    if key not in found:
                        found[key] = {"def": v, "src": f"{f.rel}:{t[j].line}", "misses": list(ev.misses)}
    return list(found.values())


def help_strings(idx: Index, ev: Evaluator, words: set, file_words: dict) -> list[dict]:
    items = []
    seen = set()

    def add(word, kind, usage, text, src, misses):
        key = (word, usage, text)
        if word in words and key not in seen:
            seen.add(key)
            items.append({"word": word, "kind": kind, "usage": usage, "text": text, "src": src, "misses": misses})

    for name, cis in idx.classes.items():
        for ci in cis:
            if ci.name == "CoreBHelp":
                continue
            for fname, (a, b, _) in ci.fields.items():
                if not re.search(r"(Help|Usage)$", fname):
                    continue
                ev.misses = []
                v = ev.eval_field(ci, fname, 0)
                if isinstance(v, str):
                    add(first_word(v), "help", v, None, f"{ci.file.rel}:{ci.file.toks[a].line} ({ci.name}.{fname})", list(ev.misses))
            for mname in ("Help", "Usage"):
                for m in ci.methods.get(mname, []):
                    t = m.file.toks
                    owners = file_words.get(m.file.rel, [])
                    binds = {p[0]: Unknown(p[0]) for p in m.params}
                    j = m.b0
                    while j < m.b1:
                        line_no = t[j].line
                        cx = Ctx(m.file, ci, m, j, binds, 0)
                        ev.misses = []
                        v = None
                        if t[j].k == "id" and t[j].v == "return":
                            semi = m.file.stmt_end(j + 1, m.b1)
                            try:
                                v = ev.ev(Parser(m.file, j + 1, semi).expr(), cx)
                            except (PErr, IndexError, KeyError):
                                v = None
                            j = semi
                        elif t[j].k == "id" and t[j].v == "Append" and m.file.is_op(j - 1, ".") and m.file.is_op(j + 1, "("):
                            semi = m.file.stmt_end(j, m.b1)
                            pieces, k = [], j
                            while k < semi:
                                if t[k].k == "id" and t[k].v == "Append" and m.file.is_op(k - 1, ".") and m.file.is_op(k + 1, "("):
                                    args = call_args_at(m.file, k + 1)
                                    if args:
                                        pieces.append(args[0][1])
                                    k = m.file.match.get(k + 1, k + 1) + 1
                                    continue
                                k += 1
                            v = "".join(cs_str(ev.ev(x, cx)) for x in pieces)
                            j = semi
                        j += 1
                        if not isinstance(v, str) or not v.strip():
                            continue
                        w = first_word(v)
                        if w not in words and len(owners) == 1:
                            w = owners[0]
                        for line in v.strip().split("\n"):
                            if line.strip() and not re.fullmatch(r"\s*<[^<>]*>\s*", line):
                                add(w, "method", line, None, f"{m.file.rel}:{line_no} ({ci.name}.{mname}())", list(ev.misses))
    for f in idx.files:
        t = f.toks
        for j, tk in enumerate(t):
            if tk.k == "str" and tk.v.startswith("usage: "):
                w = first_word(tk.v)
                if w not in words:
                    continue
                try:
                    p = Parser(f, j, len(t))
                    node = p.binary(10)
                except (PErr, IndexError, KeyError):
                    node = ("lit", tk.v)
                ev.misses = []
                v = ev.ev(node, ctx_at(ev, f, j))
                v = v if isinstance(v, str) else tk.v
                v = re.sub(r":\s*<[^<>]*>\s*$", "", v)
                add(w, "usage", v[7:], None, f"{f.rel}:{tk.line}", list(ev.misses))
            if tk.k == "id" and tk.v == "new" and j + 2 < len(t) and t[j + 1].k == "id" and f.is_op(j + 2, "{"):
                close = f.match.get(j + 2)
                if close is None or not any(t[x].k == "id" and t[x].v == "usage" and f.is_op(x + 1, "=") for x in range(j + 3, close)):
                    continue
                try:
                    node = Parser(f, j, close + 1).primary()
                except (PErr, IndexError, KeyError):
                    continue
                ev.misses = []
                v = ev.ev(node, ctx_at(ev, f, j))
                if isinstance(v, Inst) and isinstance(v.f.get("usage"), str):
                    u = v.f["usage"]
                    title = v.f.get("title")
                    prep = v.f.get("prep")
                    text = (cs_str(v.f.get("id")) + ": " if isinstance(v.f.get("id"), str) else "") + (title if isinstance(title, str) else "")
                    if isinstance(prep, str):
                        text += " (prep: " + prep + ")"
                    add(first_word(u), "entry", u, text, f"{f.rel}:{tk.line} ({v.type})", list(ev.misses))
    return items


def extension_classes(idx: Index, ev: Evaluator) -> list[dict]:
    out = []
    for name, cis in idx.classes.items():
        for ci in cis:
            if "IInsideDevExtension" not in ci.bases:
                continue
            words = []
            for m in ci.methods.get("Command", []):
                f = m.file
                body = f.toks[m.b0:m.b1]
                text = " ".join(str(x.raw) for x in body)
                words += re.findall(r'a \[ 0 \] \. ToLowerInvariant \( \) [!=]= "([^"]+)"', text)
                words += re.findall(r'a \[ 0 \] \. Equals \( "([^"]+)"', text)
                for ident in re.findall(r"string \. Equals \( a \[ 0 \] , ([\w .]+?) ,", text):
                    parts = ident.replace(" ", "").split(".")
                    v = ev.member(ClassRef(parts[0]), parts[1], ctx_at(ev, f, m.b0)) if len(parts) == 2 else ev.resolve(parts[0], ctx_at(ev, f, m.b0), m.b0)
                    if isinstance(v, str):
                        words.append(v)
            cases = []
            for m in ci.methods.get("Command", []):
                t = m.file.toks
                cases += [t[j + 1].v for j in range(m.b0, m.b1 - 2) if t[j].k == "id" and t[j].v == "case" and t[j + 1].k == "str"]
            comments = []
            lines = ci.file.text.split("\n")
            for n, line in enumerate(lines):
                s = line.strip()
                if not s.startswith("//"):
                    continue
                body = s[2:].strip()
                if not (body.startswith("Bridge / console:") or body.startswith("Console only")):
                    continue
                parts = [body]
                k = n + 1
                while k < len(lines) and lines[k].strip().startswith("//"):
                    nb = lines[k].strip()[2:].strip()
                    if parts[-1].endswith(",") or (nb[:1].islower()):
                        parts.append(nb)
                        k += 1
                        continue
                    break
                comments.append((n + 1, " ".join(parts)))
            seen = []
            for w in words:
                if w not in seen:
                    seen.append(w)
            out.append({"class": ci.name, "file": ci.file.rel, "line": ci.file.toks[ci.b0].line, "words": seen, "cases": cases, "comments": comments})
    out.sort(key=lambda x: (x["file"], x["class"]))
    return out


def schema_rows(sch, prefix: str = "") -> list[tuple]:
    rows = []
    if not isinstance(sch, dict):
        return rows
    props = sch.get("properties", {})
    required = sch.get("required", []) or []
    if not isinstance(props, dict):
        return rows
    for name, p in props.items():
        full = prefix + str(name)
        rows.append((full, p if isinstance(p, dict) else {}, name in required))
        if not isinstance(p, dict):
            continue
        if p.get("type") == "object" and p.get("properties"):
            rows += schema_rows(p, full + ".")
        it = p.get("items")
        if p.get("type") == "array" and isinstance(it, dict) and it.get("type") == "object" and it.get("properties"):
            rows += schema_rows(it, full + "[].")
    return rows


def range_text(p: dict) -> str:
    lo, hi = p.get("minimum"), p.get("maximum")
    if lo is None and hi is None:
        return ""
    return f" {fmt_num(lo) if lo is not None else ''}..{fmt_num(hi) if hi is not None else ''}"


def type_text(p: dict) -> str:
    t = p.get("type")
    if t is None:
        return "any"
    if t == "array":
        it = p.get("items") if isinstance(p.get("items"), dict) else {}
        inner = type_text(it) if it else "any"
        mn, mx = p.get("minItems"), p.get("maxItems")
        cnt = ""
        if mn is not None and mn == mx:
            cnt = f", {fmt_num(mn)} items"
        elif mn is not None and mx is not None:
            cnt = f", {fmt_num(mn)}..{fmt_num(mx)} items"
        elif mx is not None:
            cnt = f", up to {fmt_num(mx)} items"
        elif mn is not None:
            cnt = f", at least {fmt_num(mn)} items"
        return f"array of {inner}{cnt}"
    if t == "string" and p.get("maxLength") is not None:
        return f"string (max {fmt_num(p['maxLength'])} chars)"
    if t in ("integer", "number"):
        return t + range_text(p)
    return str(t)


def json_val(v) -> str:
    if isinstance(v, bool):
        return "true" if v else "false"
    if is_num(v):
        return fmt_num(v)
    if isinstance(v, str):
        return json.dumps(v, ensure_ascii=False)
    if isinstance(v, list):
        return "[" + ", ".join(json_val(x) for x in v) + "]"
    if v is None:
        return "null"
    return cs_str(v)


def profile_names(v) -> list[str]:
    val = v.value if isinstance(v, EnumVal) else v if isinstance(v, int) else None
    if val is None:
        return ["?"]
    names = []
    if val & 1:
        names.append("play")
    if val & 2:
        names.append("dev")
    return names or ["none"]


def server_profiles(idx: Index, ev: Evaluator) -> list[str]:
    out = []
    for ci in idx.classes.get("McpProfile", []):
        for fname in ci.fields:
            v = ev.eval_field(ci, fname, 0)
            if isinstance(v, str):
                out.append(v)
    return out


def grep_tool_names(idx: Index) -> set:
    names = set()
    for f in idx.files:
        if not (f.rel.startswith("src/mcp/") or f.rel.startswith("src/play/")):
            continue
        names.update(re.findall(r'\bT\(\s*"(\w+)"', f.text))
        names.update(re.findall(r'PlayShared\.Def\(\s*"(\w+)"', f.text))
        names.update(re.findall(r'new ToolDef\s*\{\s*name\s*=\s*"(\w+)"', f.text))
        for const in re.findall(r"PlayShared\.Def\(\s*(\w+)\s*,", f.text):
            m = re.search(rf'const string[^;]*\b{const}\s*=\s*"(\w+)"', f.text)
            if m:
                names.add(m.group(1))
    return names


def main(argv: list[str]) -> int:
    if len(argv) != 3:
        print(__doc__.strip().split("\n")[-1], file=sys.stderr)
        return 2
    root, out_path = Path(argv[1]), Path(argv[2])
    if not (root / "src").is_dir():
        print(f"not a source tree (no src folder): {root}", file=sys.stderr)
        return 2
    idx = Index(root)
    ev = Evaluator(idx)
    problems: list[str] = []

    cmds, help_file = extract_commands(idx, ev)
    config, describe_sites = extract_config(idx, ev)
    exts = extension_classes(idx, ev)
    tools = extract_tools(idx, ev)
    profiles = server_profiles(idx, ev)

    forwarded = [c.f["name"] for c in cmds if c.f.get("forwarded") is True]
    ext_words = [w for e in exts for w in e["words"]]
    file_words = {}
    for e in exts:
        file_words.setdefault(e["file"], []).extend(e["words"])
    word_set = set(ext_words) | set(forwarded)
    hitems = help_strings(idx, ev, word_set, file_words)

    main_help = {}
    for it in hitems:
        if it["kind"] in ("help", "method"):
            main_help.setdefault(it["word"], []).append(it["usage"])
    for it in list(hitems):
        if it["kind"] == "usage":
            body = it["usage"]
            core = re.sub(r"\s+\(.*$", "", body).strip()
            if any(core and core in h for h in main_help.get(it["word"], [])):
                hitems.remove(it)

    word_order = []
    for w in ext_words + forwarded:
        if w not in word_order and any(it["word"] == w for it in hitems) or (w in ext_words and w not in word_order):
            if w not in word_order:
                word_order.append(w)
    no_help = [w for w in forwarded if w not in word_order]

    anchors = Anchors()
    L: list[str] = []
    title = "InsideDev 0.11.0 command, config and MCP tool reference"
    anchors.make(title)

    groups: list[str] = []
    for c in cmds:
        if c.f["group"] not in groups:
            groups.append(c.f["group"])
    n_console = sum(1 for c in cmds if c.f.get("console") is True)
    n_bridge = len(cmds) - n_console

    parts: dict[str, list[dict]] = {}
    for c in config:
        parts.setdefault(cs_str(c["part"]), []).append(c)
    unique_keys = {cs_str(c["key"]) for c in config}
    key_parts: dict[str, list[str]] = {}
    for c in config:
        key_parts.setdefault(cs_str(c["key"]), [])
        if cs_str(c["part"]) not in key_parts[cs_str(c["key"])]:
            key_parts[cs_str(c["key"])].append(cs_str(c["part"]))

    def tool_profiles(t: dict) -> list[str]:
        d = t["def"]
        if d.f.get("owner") == "SERVER":
            return ["launcher"]
        return profile_names(d.f.get("profiles"))

    tools.sort(key=lambda t: (t["def"].f["name"], t["src"]))
    play_tools = [t for t in tools if "play" in tool_profiles(t)]
    dev_tools = [t for t in tools if "dev" in tool_profiles(t)]
    launcher_tools = [t for t in tools if "launcher" in tool_profiles(t)]
    ext_rows = sum(1 for it in hitems)

    sec = ["1. Bridge and console commands", "2. Extension commands", "3. Config keys", "4. MCP tools"]
    sec_a = {}

    L.append(f"# {title}")
    L.append("")
    L.append("Generated from the source code of InsideDev 0.11.0 by gen_commands.py; do not edit by hand.")
    L.append("")
    L.append(f"- Bridge and console commands: {len(cmds)} ({n_bridge} bridge commands, {n_console} console words; {len(forwarded)} forwarded), from {help_file.rel}")
    L.append(f"- Extension commands: {len(word_order)} words, {ext_rows} usage lines, from {len(exts)} extension classes and the forwarded rows")
    L.append(f"- Config keys: {len(config)} entries from {describe_sites} Describe call sites (loops expanded), {len(unique_keys)} distinct keys, in {len(parts)} parts")
    prof_counts = ", ".join([f"play {len(play_tools)}", f"dev {len(dev_tools)}", f"all {len([t for t in tools if t not in launcher_tools])}"])
    L.append(f"- MCP tools: {len(tools) - len(launcher_tools)} tools served by the game ({prof_counts}), plus {len(launcher_tools)} answered by the stdio launcher")
    L.append("")
    L.append("## Contents")
    L.append("")
    for s in sec:
        sec_a[s] = anchors.make(s)
        L.append(f"- [{s}](#{sec_a[s]})")
    L.append("")

    L.append(f"## {sec[0]}")
    L.append("")
    L.append(f"The command table of {help_file.rel}. Every word works through the file bridge, the MCP command tool and the in-game console. "
             "Marker console: a word of the in-game console that the bridge passes on. Marker forwarded: handed to another part that has its own argument checks and help (see section 2). "
             "Min args: the least number of arguments a call needs.")
    L.append("")
    L.append("Groups: " + ", ".join(f"[{g}](#{re.sub(r'[^a-z0-9 -]', '', ('commands: ' + g).lower()).replace(' ', '-')})" for g in groups))
    L.append("")
    for g in groups:
        h = f"Commands: {g}"
        L.append(f"### {h}")
        anchors.make(h)
        L.append("")
        L.append("| Command | Usage | Text | Min args | Marker |")
        L.append("|---|---|---|---|---|")
        for c in cmds:
            if c.f["group"] != g:
                continue
            name = c.f["name"]
            usage = c.f.get("usage")
            u = code(usage) if isinstance(usage, str) else ("see [" + name + "](#ext-" + name + ")" if name in word_order else "")
            marks = []
            if c.f.get("console") is True:
                marks.append("console")
            if c.f.get("forwarded") is True:
                marks.append(f"forwarded ([{name}](#ext-{name}))" if name in word_order else "forwarded")
            mn = c.f.get("min")
            L.append(f"| {code(name)} | {u} | {esc_text(c.f.get('text'))} | {mn if is_num(mn) and mn > 0 else ''} | {', '.join(marks)} |")
        L.append("")

    L.append(f"## {sec[1]}")
    L.append("")
    L.append("Words added by the extension DLLs (classes that implement IInsideDevExtension; the core hands every word it does not know to them), "
             "and the sub-command help of the forwarded rows of section 1. Usage lines come from the Help and Usage string constants, the Help / Usage methods, "
             "the \"usage: ...\" replies and the test tables in the named source files; a usage reply already contained in the main help is not repeated.")
    L.append("")
    L.append("| Extension class | Source | Words |")
    L.append("|---|---|---|")
    for e in exts:
        L.append(f"| {code(e['class'])} | {e['file']} | {', '.join(code(w) for w in e['words']) or 'none found'} |")
    L.append("")
    for e in exts:
        for line, text in e["comments"]:
            L.append(f"- Source comment in {e['file']} (line {line}): {code(text)}")
    if any(e["comments"] for e in exts):
        L.append("")
    if no_help:
        L.append("Forwarded rows of section 1 without separate help text in the code: " + ", ".join(code(w) for w in no_help) + ".")
        L.append("")
    for w in word_order:
        owner = next((e for e in exts if w in e["words"]), None)
        L.append(f'<a id="ext-{w}"></a>')
        L.append("")
        h = f"Word: {w}"
        anchors.make(h)
        L.append(f"### {h}")
        L.append("")
        if owner:
            L.append(f"Dispatched by {owner['class']} in {owner['file']}.")
        else:
            L.append("A forwarded row of section 1 (handled by the core).")
        L.append("")
        rows = [it for it in hitems if it["word"] == w]
        if owner and not any(it["kind"] in ("help", "method") for it in rows) and len(owner["words"]) == 1 and owner["cases"]:
            L.append(f"Sub-commands handled in {owner['file']} (case labels of the dispatcher): " + ", ".join(code(c) for c in owner["cases"]) + ".")
            L.append("")
        if not rows:
            L.append("No usage text found in the code.")
            L.append("")
            continue
        L.append("| Usage | Text | Source |")
        L.append("|---|---|---|")
        for it in rows:
            if it["kind"] in ("help", "method"):
                for line in it["usage"].split("\n"):
                    s = line.strip()
                    if not s:
                        continue
                    body = s[7:] if s.startswith("usage: ") else s
                    if body.split(" ", 1)[0] == w:
                        L.append(f"| {code(body)} |  | {it['src']} |")
                    else:
                        L.append(f"|  | {esc_text(s)} | {it['src']} |")
            else:
                L.append(f"| {code(it['usage'].strip())} | {esc_text(it['text'] or '')} | {it['src']} |")
        L.append("")

    L.append(f"## {sec[2]}")
    L.append("")
    L.append("Keys of _mod\\\\editor.cfg as described by EditorState.Describe(part, key, default, text) (cfg lists them). "
             "Calls inside foreach loops are expanded to one row per value. "
             f"Total: {len(config)} rows, {len(unique_keys)} distinct keys "
             f"(a key described by two parts is listed under both).")
    L.append("")
    L.append("Parts: " + ", ".join(f"{p} ({len(v)})" for p, v in parts.items()))
    L.append("")
    for p, rows in parts.items():
        h = f"Part: {p}"
        anchors.make(h)
        L.append(f"### {h}")
        L.append("")
        L.append("| Key | Default | Meaning | Source |")
        L.append("|---|---|---|---|")
        for r in rows:
            d = r["def"]
            dtxt = '`""`' if d == "" else code(cs_str(d))
            k = cs_str(r["key"])
            others = [x for x in key_parts.get(k, []) if x != p]
            note = f" (also described by part {', '.join(others)})" if others else ""
            L.append(f"| {code(k)} | {dtxt} | {esc_text(r['text'])}{esc_text(note)} | {r['src']} |")
        L.append("")

    L.append(f"## {sec[3]}")
    L.append("")
    L.append("Tools of the in-game MCP server. Profiles of the server: " + ", ".join(code(p) for p in profiles) +
             " (endpoints /mcp or /mcp/dev, /mcp/play, /mcp/all; all serves every tool). "
             "Arguments come from the input schema builders (src/mcp/ServerSchema.cs); nested objects and array items are flattened with dotted names.")
    L.append("")
    L.append("| Tool | Profiles | Title | Source |")
    L.append("|---|---|---|---|")
    for t in tools:
        d = t["def"]
        nm = d.f["name"]
        L.append(f"| [{code(nm)}](#tool-{nm}) | {', '.join(tool_profiles(t))} | {esc_text(d.f.get('title'))} | {t['src']} |")
    L.append("")
    for t in tools:
        d = t["def"]
        nm = d.f["name"]
        L.append(f'<a id="tool-{nm}"></a>')
        L.append("")
        h = f"Tool: {nm}"
        anchors.make(h)
        L.append(f"### {h}")
        L.append("")
        ann = [a for a, k in (("read-only", "readOnly"), ("destructive", "destructive"), ("idempotent", "idempotent")) if d.f.get(k) is True]
        meta = d.f.get("meta")
        if isinstance(meta, dict) and meta.get("anthropic/alwaysLoad") is True:
            ann.append("always loaded")
        prof = tool_profiles(t)
        prof_text = "every profile (answered by the stdio launcher, src/mcp/McpStdio.cs)" if prof == ["launcher"] else ", ".join(prof)
        L.append(f"- Profiles: {prof_text}")
        L.append(f"- Title: {esc_text(d.f.get('title'))}")
        L.append(f"- Owner: {esc_text(d.f.get('owner'))}")
        if ann:
            L.append(f"- Annotations: {', '.join(ann)}")
        L.append(f"- Source: {t['src']}")
        L.append("")
        L.append(esc_text(d.f.get("description")))
        L.append("")
        sch = d.f.get("inputSchema")
        rows = schema_rows(sch)
        if not isinstance(sch, dict):
            L.append(f"Input schema could not be read statically ({esc_text(cs_str(sch))}).")
            problems.append(f"tool {nm}: input schema not read ({t['src']})")
            L.append("")
            continue
        if not rows:
            L.append("No arguments.")
            L.append("")
            continue
        L.append("| Argument | Type | Required / default | Enum | Description |")
        L.append("|---|---|---|---|---|")
        for name, p, req in rows:
            rd = "required" if req else (f"default {code(json_val(p['default']))}" if "default" in p else "")
            en = p.get("enum")
            if en is None and isinstance(p.get("items"), dict):
                en = p["items"].get("enum")
            ent = ", ".join(code(cs_str(x)) for x in en) if isinstance(en, list) else ""
            L.append(f"| {code(name)} | {esc_text(type_text(p))} | {rd} | {ent} | {esc_text(p.get('description', ''))} |")
        L.append("")

    text = "\n".join(L).rstrip() + "\n"
    if "—" in text:
        problems.append("output contains an em-dash")
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(text, encoding="utf-8", newline="\n")

    src_help = help_file.text
    c_rows = len(re.findall(r"^\s*C\(", src_help, re.M))
    k_rows = len(re.findall(r"^\s*K\(", src_help, re.M))
    print(f"commands: {len(cmds)} ({n_bridge} C rows, {n_console} K rows); source lines: {c_rows} C(, {k_rows} K(")
    if c_rows != n_bridge or k_rows != n_console:
        problems.append("command count differs from the C( / K( lines of CoreBHelp.cs")
    grep_sites = 0
    for f in idx.files:
        grep_sites += len(re.findall(r"EditorState\.Describe\(", f.text))
        if f.rel.endswith("Editor/EditorState.cs"):
            grep_sites += len(re.findall(r"^\s*Describe\(", f.text, re.M))
    print(f"config: {len(config)} rows from {describe_sites} call sites (grep: {grep_sites}), {len(unique_keys)} distinct keys")
    for p, v in parts.items():
        print(f"  part {p}: {len(v)}")
    if grep_sites != describe_sites:
        problems.append(f"Describe call sites: parsed {describe_sites}, grep {grep_sites}")
    print(f"extension: {len(exts)} classes, words {', '.join(word_order)}; {ext_rows} usage items")
    print(f"mcp: {len(tools)} tools; play {len(play_tools)}, dev {len(dev_tools)}, launcher {len(launcher_tools)}")
    names_out = {t['def'].f['name'] for t in tools}
    missing = sorted(grep_tool_names(idx) - names_out)
    if missing:
        problems.append("tool names found by grep but not extracted: " + ", ".join(missing))
    for c in config:
        for v in (c["part"], c["key"], c["def"], c["text"]):
            if not isinstance(v, str):
                problems.append(f"config {c['src']}: value not resolved: {cs_str(v)}")
        for m, where in c["misses"]:
            problems.append(f"config {c['src']}: {m}")
    for t in tools:
        for m, where in t["misses"]:
            problems.append(f"tool {t['def'].f['name']} ({t['src']}): {m} [{where}]")
        for name, p, req in schema_rows(t["def"].f.get("inputSchema")):
            for k in ("description", "default"):
                if isinstance(p.get(k), Unknown):
                    problems.append(f"tool {t['def'].f['name']} ({t['src']}): {name}.{k} unresolved: {p[k].text}")
    for it in hitems:
        for m, where in it["misses"]:
            problems.append(f"help {it['word']} ({it['src']}): {m}")
    for c in cmds:
        for k in ("group", "name", "text"):
            if not isinstance(c.f.get(k), str):
                problems.append(f"command field {k} unresolved: {cs_str(c.f.get(k))}")
    seen = set()
    for p in problems:
        if p not in seen:
            seen.add(p)
            print("PROBLEM: " + p)
    print(f"wrote {out_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
