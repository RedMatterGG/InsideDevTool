#!/usr/bin/env python3
"""inside_ctl.py: plain command-line control of INSIDE through InsideDev, no MCP client (docs/MCP_PLAY_DESIGN.md 18-19).

Commands (options go before the command):
  inside_ctl.py <command ...>     one console / bridge command (ctl run right 1.2, ctl status, boypos ...) through
                                  POST /cmd on the game's port; when nothing listens there (or the game's InsideDev.Mcp.dll
                                  has no /cmd), through the file bridge (_mod/cmd). A timed ctl command answers
                                  "started: ... job=jN"; the tool then follows it with ctl job jN until it ends (--no-wait)
  inside_ctl.py state             the concise observe JSON (GET /state; through the bridge: ctl status json)
  inside_ctl.py seq "<script>"    ctl seq "<script>" (e.g. "R1.2 J R0.4+J G1 W0.5 L0.3"), waits for the job
  inside_ctl.py ping              GET /ping (bridge: ping)
  inside_ctl.py repl              line-by-line console; every line is one command; quit, exit or end of input stops
  inside_ctl.py cmd <line ...>    send the words as they are (for a console command named state, seq, repl, ping or cmd)
Options: --port N (47811, env INSIDE_MCP_PORT)  --host H (127.0.0.1)  --token T (env INSIDEDEV_MCP_TOKEN)
  --game DIR (env INSIDE_GAME: the INSIDE folder, bridge = DIR/_mod/cmd)  --bridge DIR (env INSIDE_BRIDGE, wins over --game)
  --http-only  --bridge-only  --no-wait
  --timeout S (how long to follow a job, 120)
Ctrl+C while a job is followed sends ctl stop. Exit: 0 ok, 1 the command failed (busy, usage, refused, failed, not found,
unknown ...), 2 the game is not reachable (or the request may have run but no reply came back).
"""
from __future__ import annotations

import argparse
import os
import re
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path

DEFAULT_PORT = 47811
ERROR_WORDS = ("busy:", "busy ", "usage:", "usage ", "refused", "failed:", "not found", "unknown ", "error ", "cannot ")
JOB_RE = re.compile(r"\bjob=(j\d+)\b")
RESERVED = ("state", "seq", "repl", "ping", "cmd")
EXIT_OK, EXIT_FAILED, EXIT_UNREACHABLE = 0, 1, 2


class Unreachable(Exception):
    pass


class Lost(Exception):
    pass


@dataclass
class Reply:
    text: str
    error: str | None
    via: str

    @property
    def first(self) -> str:
        return self.text.split("\n", 1)[0] if self.text else ""

    @property
    def failed(self) -> bool:
        return self.error is not None or self.first.lower().startswith(ERROR_WORDS)


def quote(words: list[str]) -> str:
    out = []
    for w in words:
        w = w.replace('"', "")
        out.append(f'"{w}"' if (" " in w or "\t" in w) else w)
    return " ".join(out)


def refused(e: BaseException) -> bool:
    r = getattr(e, "reason", e)
    if isinstance(r, (ConnectionRefusedError, ConnectionAbortedError)):
        return True
    text = str(r).lower()
    return "refused" in text or "10061" in text or "getaddrinfo" in text or "name or service" in text


@dataclass
class Link:
    host: str = "127.0.0.1"
    port: int = DEFAULT_PORT
    token: str = ""
    bridge: Path | None = None
    use_http: bool = True
    use_bridge: bool = True
    http_timeout: float = 70.0
    bridge_timeout: float = 20.0
    http_down: bool = False

    def http(self, method: str, path: str, body: str | None) -> tuple[int, str, dict[str, str]]:
        data = body.encode("utf-8") if body is not None else None
        headers = {"Content-Type": "text/plain; charset=utf-8"} if data is not None else {}
        if self.token:
            headers["Authorization"] = "Bearer " + self.token
        req = urllib.request.Request(f"http://{self.host}:{self.port}{path}", data=data, headers=headers, method=method)
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        try:
            with opener.open(req, timeout=self.http_timeout) as r:
                return r.status, r.read().decode("utf-8", "replace"), {k.lower(): v for k, v in r.headers.items()}
        except urllib.error.HTTPError as e:
            return e.code, e.read().decode("utf-8", "replace"), {k.lower(): v for k, v in (e.headers or {}).items()}
        except (urllib.error.URLError, OSError) as e:
            if refused(e):
                raise Unreachable(f"nothing listens on http://{self.host}:{self.port} ({getattr(e, 'reason', e)})") from e
            raise Lost(f"http://{self.host}:{self.port}{path}: {getattr(e, 'reason', e)}; the command may have run") from e

    def send(self, command: str) -> Reply:
        if self.use_http and not self.http_down:
            try:
                code, text, headers = self.http("POST", "/cmd", command)
                if code == 200:
                    return Reply(text.rstrip("\n"), headers.get("x-insidedev-error"), "http")
                if code != 404:
                    return Reply(text.rstrip("\n"), f"http {code}", "http")
                self.http_down = True
            except Unreachable:
                self.http_down = True
                if not self.use_bridge:
                    raise
        if not self.use_bridge:
            raise Unreachable(f"http://{self.host}:{self.port} has no /cmd (an older InsideDev.Mcp.dll) and the bridge is off")
        return self.via_bridge(command)

    def via_bridge(self, command: str) -> Reply:
        d = self.bridge
        if d is None:
            raise Unreachable(f"no game: http://{self.host}:{self.port} is down and no bridge folder is set (--game DIR or --bridge DIR, env INSIDE_GAME / INSIDE_BRIDGE)")
        if not d.is_dir():
            raise Unreachable(f"no game: http://{self.host}:{self.port} is down and the bridge folder {d} does not exist")
        out = d / "out.txt"
        start = out.stat().st_size if out.exists() else 0
        rid = f"c{os.getpid() % 1000:03d}{time.time_ns() // 1_000_000 % 1_000_000:06d}"
        tmp = d / f"ctl_{rid}.tmp"
        with tmp.open("w", encoding="utf-8", newline="\n") as f:
            f.write(f"{rid}|{command}\n")
        req = d / f"in_{rid}.txt"
        os.replace(tmp, req)
        head, tail = f">>> {rid}  ", f"<<< {rid}"
        deadline = time.monotonic() + self.bridge_timeout
        while time.monotonic() < deadline:
            time.sleep(0.1)
            if not out.exists():
                continue
            size = out.stat().st_size
            if size < start:
                start = 0
            with out.open("rb") as f:
                f.seek(start)
                chunk = f.read().decode("utf-8", "replace")
            i = chunk.find(head)
            if i < 0:
                continue
            j = chunk.find("\n" + tail, i)
            if j < 0:
                continue
            lines = chunk[i:j].split("\n")[1:]
            body = [ln[len("  log: "):] if ln.startswith("  log: ") else ln for ln in lines]
            return Reply("\n".join(body).rstrip("\n"), None, "bridge")
        if req.exists():
            req.unlink(missing_ok=True)
            raise Unreachable(f"the bridge {d} did not pick the command up in {self.bridge_timeout:.0f} s{self.stale_note()}")
        raise Lost(f"the bridge took the command but wrote no reply in {self.bridge_timeout:.0f} s; it may have run")

    def stale_note(self) -> str:
        if self.bridge is None:
            return ""
        st = self.bridge / "state.txt"
        if not st.exists():
            return " (no state.txt: the game does not seem to run)"
        age = time.time() - st.stat().st_mtime
        return f" (state.txt is {age:.0f} s old: the game does not seem to run)" if age > 15 else ""

    def get(self, path: str, bridge_command: str) -> Reply:
        if self.use_http and not self.http_down:
            try:
                code, text, _ = self.http("GET", path, None)
                if code != 404:
                    return Reply(text.rstrip("\n"), None if code == 200 else f"http {code}", "http")
                self.http_down = True
            except Unreachable:
                self.http_down = True
                if not self.use_bridge:
                    raise
        if not self.use_bridge:
            raise Unreachable(f"http://{self.host}:{self.port}{path} is not served and the bridge is off")
        return self.via_bridge(bridge_command)


def follow(link: Link, reply: Reply, timeout: float, wait: bool, show) -> Reply:
    m = JOB_RE.search(reply.first)
    if not wait or reply.failed or not reply.first.startswith("started:") or m is None:
        return reply
    show(reply.first)
    job = m.group(1)
    deadline = time.monotonic() + timeout
    pause = 0.1 if reply.via == "http" else 0.4
    try:
        while True:
            time.sleep(pause)
            r = link.send(f"ctl job {job}")
            if not r.first.startswith("running:"):
                return r
            if time.monotonic() > deadline:
                return Reply(r.text + f"\n(still running after {timeout:.0f} s; ctl job {job} follows it, ctl stop cancels it)", "timeout", r.via)
    except KeyboardInterrupt:
        r = link.send("ctl stop")
        return Reply(r.text, "interrupted", r.via)


def run_one(link: Link, command: str, timeout: float, wait: bool) -> int:
    reply = follow(link, link.send(command), timeout, wait, print)
    if reply.text:
        print(reply.text)
    return EXIT_FAILED if reply.failed else EXIT_OK


def repl(link: Link, timeout: float, wait: bool) -> int:
    try:
        import readline  # noqa: F401
    except ImportError:
        pass
    code = EXIT_OK
    while True:
        try:
            line = input("inside> ").strip()
        except (EOFError, KeyboardInterrupt):
            print()
            return code
        if not line:
            continue
        if line.lower() in ("quit", "exit"):
            return code
        try:
            code = run_one(link, line, timeout, wait)
        except Unreachable as e:
            print(f"unreachable: {e}")
            code = EXIT_UNREACHABLE
        except Lost as e:
            print(f"lost: {e}")
            code = EXIT_UNREACHABLE


def parse(argv: list[str]) -> argparse.Namespace:
    p = argparse.ArgumentParser(prog="inside_ctl.py", description="Plain command-line control of INSIDE through InsideDev (POST /cmd, file bridge fallback).",
                                epilog="Options go before the command. Example: inside_ctl.py ctl run right 1.2")
    p.add_argument("--port", type=int, default=int(os.environ.get("INSIDE_MCP_PORT", DEFAULT_PORT)))
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--token", default=os.environ.get("INSIDEDEV_MCP_TOKEN", ""))
    p.add_argument("--game", type=Path, default=Path(os.environ["INSIDE_GAME"]) if os.environ.get("INSIDE_GAME") else None,
                   help="the INSIDE folder; the file bridge is <game>/_mod/cmd (env INSIDE_GAME)")
    p.add_argument("--bridge", type=Path, default=Path(os.environ["INSIDE_BRIDGE"]) if os.environ.get("INSIDE_BRIDGE") else None,
                   help="the file bridge folder <game>/_mod/cmd (env INSIDE_BRIDGE; wins over --game)")
    g = p.add_mutually_exclusive_group()
    g.add_argument("--http-only", action="store_true", help="never use the file bridge")
    g.add_argument("--bridge-only", action="store_true", help="never use HTTP")
    p.add_argument("--no-wait", action="store_true", help="do not follow a started ctl job")
    p.add_argument("--timeout", type=float, default=120.0, help="seconds to follow a job")
    p.add_argument("words", nargs=argparse.REMAINDER, help="the command")
    a = p.parse_args(argv)
    if not a.words:
        p.error("give a command, state, seq \"<script>\", ping or repl")
    return a


def main(argv: list[str]) -> int:
    a = parse(argv)
    bridge = a.bridge if a.bridge is not None else (a.game / "_mod" / "cmd" if a.game is not None else None)
    link = Link(host=a.host, port=a.port, token=a.token, bridge=bridge, use_http=not a.bridge_only, use_bridge=not a.http_only)
    wait = not a.no_wait
    head, rest = a.words[0].lower(), a.words[1:]
    try:
        if head == "repl" and not rest:
            return repl(link, a.timeout, wait)
        if head == "state" and not rest:
            r = link.get("/state", "ctl status json")
            print(r.text)
            return EXIT_FAILED if r.error else EXIT_OK
        if head == "ping" and not rest:
            r = link.get("/ping", "ping")
            print(r.text)
            return EXIT_FAILED if r.error else EXIT_OK
        if head == "seq":
            if not rest:
                print('usage: inside_ctl.py seq "<script>"  (e.g. "R1.2 J R0.4+J G1 W0.5 L0.3")')
                return EXIT_FAILED
            return run_one(link, quote(["ctl", "seq", " ".join(rest)]), a.timeout, wait)
        if head == "cmd":
            if not rest:
                print("usage: inside_ctl.py cmd <line ...>")
                return EXIT_FAILED
            return run_one(link, quote(rest), a.timeout, wait)
        return run_one(link, quote(a.words), a.timeout, wait)
    except Unreachable as e:
        print(f"unreachable: {e}", file=sys.stderr)
        return EXIT_UNREACHABLE
    except Lost as e:
        print(f"lost: {e}", file=sys.stderr)
        return EXIT_UNREACHABLE


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
