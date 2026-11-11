#!/usr/bin/env python3
"""inside_ctl.py: plain command-line control of INSIDE through InsideDev, no MCP client (docs/MCP_PLAY_DESIGN.md 18-19).

Commands (options go before the command):
  inside_ctl.py <command ...>     one console / bridge command (ctl run right 1.2, ctl status, boypos ...) through
                                  POST /cmd on the game's port; when nothing listens there (or the game's InsideDev.Mcp.dll
                                  has no /cmd), through the file bridge (_mod/cmd). A timed ctl command answers
                                  "started: ... job=jN"; the tool then follows it with ctl job jN until it ends (--no-wait).
                                  One argument with blanks is a whole command line and is sent as typed
                                  ("srec record status"); several arguments are words (a word with a blank is quoted)
  inside_ctl.py state             the concise observe JSON (GET /state; through the bridge: ctl status json); needs
                                  InsideDev.Play.dll
  inside_ctl.py snapshot          the text snapshot of the bridge (the console command state: version, character,
                                  savepoint, streaming ...); needs only the core
  inside_ctl.py wait [up | ready | savepoint <subscene> <savepoint>] [--timeout S]
                                  block until the game answers (up), has a loaded world (ready, the default) or stands
                                  at that savepoint; S defaults to 120; exit 2 when the time runs out
  inside_ctl.py doctor [json]     the game's own check (the console command doctor); when the game is not reachable:
                                  what can be seen from outside (is INSIDE.exe running, is the Steam client running, is
                                  the port open, is the window minimized, the bridge folder)
  inside_ctl.py seq "<script>"    ctl seq "<script>" (e.g. "R1.2 J R0.4+J G1 W0.5 L0.3"), waits for the job
  inside_ctl.py ping              GET /ping (bridge: ping)
  inside_ctl.py repl              line-by-line console; every line is one command; quit, exit or end of input stops
  inside_ctl.py -f FILE           the lines of FILE (or of standard input with -f -) as commands; # starts a comment
  inside_ctl.py cmd <line ...>    send the words as they are (for a console command named like one of the above)
Options: --port N (47811, env INSIDE_MCP_PORT)  --host H (127.0.0.1)  --token T (env INSIDEDEV_MCP_TOKEN)
  --game DIR (env INSIDE_GAME: the INSIDE folder, bridge = DIR/_mod/cmd)  --bridge DIR (env INSIDE_BRIDGE, wins over --game;
  without both the folder of a running INSIDE.exe is used)  --http-only  --bridge-only  --force-bridge  --no-wait
  --timeout S (how long to follow a job, 120)  --http-timeout S (70)  --bridge-timeout S (20)
  --json (one JSON line per command: ok, text, error, via, job, exit)  --stop-on-error (repl, -f)
  --quote-all (quote every argument that has a blank, also a single one)
Ctrl+C while a job is followed sends ctl stop. Exit: 0 ok, 1 the command failed (busy, usage, failed, not found, unknown ...),
2 the game is not reachable (or the request may have run but no reply came back), 3 the game refused the command (refused,
not allowed, wrong or missing token), 130 interrupted.
"""
from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import inside_link as IL  # noqa: E402
from inside_link import EXIT_FAILED, EXIT_OK, EXIT_UNREACHABLE, Link, Lost, Reply, Unreachable, quote_words  # noqa: E402

RESERVED = ("state", "snapshot", "wait", "doctor", "seq", "repl", "ping", "cmd")
EXIT_INTERRUPTED = 130
quote = quote_words


class Out:
    def __init__(self, as_json: bool):
        self.as_json = as_json

    def reply(self, command: str, reply: Reply, started: str | None = None, code: int | None = None) -> int:
        code = reply.exit_code if code is None else code
        if self.as_json:
            job = IL.JOB_RE.search(started or "")
            print(json.dumps({"ok": code == EXIT_OK, "text": reply.text, "error": reply.error, "via": reply.via,
                              "job": job.group(1) if job else None, "exit": code, "command": command}, ensure_ascii=False), flush=True)
        elif reply.text:
            print(reply.text, flush=True)
        return code

    def problem(self, command: str, kind: str, text: str, code: int) -> int:
        if self.as_json:
            print(json.dumps({"ok": False, "text": "", "error": f"{kind}: {text}", "via": None, "job": None, "exit": code,
                              "command": command}, ensure_ascii=False), flush=True)
        else:
            print(f"{kind}: {text}", file=sys.stderr, flush=True)
        return code


def run_one(link: Link, command: str, timeout: float, wait: bool, out: Out) -> int:
    started: list[str] = []

    def show(line: str) -> None:
        started.append(line)
        if not out.as_json:
            print(line, flush=True)

    reply = link.run(command, wait, timeout, show)
    return out.reply(command, reply, started[0] if started else (reply.first if reply.first.startswith("started:") else None))


def guarded(link: Link, command: str, timeout: float, wait: bool, out: Out) -> int:
    try:
        return run_one(link, command, timeout, wait, out)
    except Unreachable as e:
        return out.problem(command, "unreachable", str(e), EXIT_UNREACHABLE)
    except Lost as e:
        return out.problem(command, "lost", str(e), EXIT_UNREACHABLE)


def batch(link: Link, lines, timeout: float, wait: bool, out: Out, stop_on_error: bool, prompt: str = "", quits: bool = False) -> int:
    worst = EXIT_OK
    while True:
        try:
            line = lines(prompt)
        except (EOFError, KeyboardInterrupt):
            if prompt:
                print()
            return worst
        if line is None:
            return worst
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        if quits and line.lower() in ("quit", "exit"):
            return worst
        code = guarded(link, line, timeout, wait, out)
        worst = max(worst, code)
        if code != EXIT_OK and stop_on_error:
            return worst


def repl(link: Link, timeout: float, wait: bool, out: Out, stop_on_error: bool) -> int:
    if sys.stdin.isatty():
        try:
            import readline  # noqa: F401
        except ImportError:
            pass
        return batch(link, input, timeout, wait, out, stop_on_error, "inside> ", True)
    return batch(link, lambda _p: sys.stdin.readline() or None, timeout, wait, out, stop_on_error, "", True)


def from_file(link: Link, path: str, timeout: float, wait: bool, out: Out, stop_on_error: bool) -> int:
    try:
        text = sys.stdin.read() if path == "-" else Path(path).read_text(encoding="utf-8-sig")
    except (OSError, UnicodeDecodeError) as e:
        return out.problem(f"-f {path}", "error", f"cannot read {path}: {e}", EXIT_FAILED)
    rows = iter(text.splitlines())
    return batch(link, lambda _p: next(rows, None), timeout, wait, out, stop_on_error)


def take_timeout(rest: list[str], default: float) -> tuple[list[str], float]:
    out, limit, i = [], default, 0
    while i < len(rest):
        w = rest[i]
        if w == "--timeout" and i + 1 < len(rest):
            limit, i = float(rest[i + 1]), i + 2
            continue
        if w.startswith("--timeout="):
            limit, i = float(w.split("=", 1)[1]), i + 1
            continue
        out.append(w)
        i += 1
    return out, limit


def wait_for(link: Link, rest: list[str], default: float, out: Out) -> int:
    usage = "usage: inside_ctl.py wait [up | ready | savepoint <subscene> <savepoint>] [--timeout S]"
    try:
        words, limit = take_timeout(rest, default)
    except ValueError:
        return out.problem("wait", "usage", usage[7:], EXIT_FAILED)
    what = words[0].lower() if words else "ready"
    command = "wait " + " ".join(words) if words else "wait"
    if what in ("up", "ready") and len(words) <= 1:
        r = link.wait_ready(limit, world=what == "ready")
        if r:
            return out.reply(command, Reply(f"{what}: {r.text} ({r.seconds:.1f} s)", None, r.via or "http"))
        return out.problem(command, "not " + what, f"after {r.seconds:.0f} s: {r.text}", EXIT_UNREACHABLE)
    if what == "savepoint" and len(words) == 3:
        want = f"(spawn {words[1]} {words[2]})"
        t0 = time.monotonic()
        seen = "no answer"
        while True:
            try:
                s = link.snapshot()
                line = next((ln for ln in s.text.split("\n") if ln.startswith("savepoint:")), "")
                if s.ok and want in line:
                    return out.reply(command, Reply(f"{line.strip()} ({time.monotonic() - t0:.1f} s)", None, s.via))
                seen = line.strip() or s.first or "no savepoint line"
            except IL.LinkError as e:
                seen = str(e)
            if time.monotonic() - t0 >= limit:
                return out.problem(command, "not there", f"after {limit:.0f} s: {seen}", EXIT_UNREACHABLE)
            time.sleep(0.5)
    return out.problem(command, "usage", usage[7:], EXIT_FAILED)


def print_checks(link: Link, out: Out, head: str) -> None:
    rows = IL.local_checks(link)
    if out.as_json:
        print(json.dumps({"ok": False, "error": head, "local": [{"status": s, "name": n, "detail": d} for s, n, d in rows]},
                         ensure_ascii=False), flush=True)
        return
    print(head, file=sys.stderr, flush=True)
    print("seen from outside the game:")
    for status, name, detail in rows:
        print(f"  {status:<5}{name}: {detail}")


def doctor(link: Link, rest: list[str], out: Out) -> int:
    command = quote_words(["doctor"] + rest)
    try:
        reply = link.send(command)
    except Unreachable as e:
        print_checks(link, out, f"unreachable: {e}")
        return EXIT_UNREACHABLE
    except Lost as e:
        print_checks(link, out, f"lost: {e}")
        return EXIT_UNREACHABLE
    code = reply.exit_code
    if code == EXIT_OK and reply.first.lower().startswith("doctor: fail"):
        code = EXIT_FAILED
    code = out.reply(command, reply, code=code)
    if reply.failed and reply.first.lower().startswith("unknown"):
        print_checks(link, out, "this game build has no doctor command (InsideDev before 0.11.0)")
    return code


def parse(argv: list[str]) -> argparse.Namespace:
    p = argparse.ArgumentParser(prog="inside_ctl.py", description="Plain command-line control of INSIDE through InsideDev (POST /cmd, file bridge fallback).",
                                epilog="Options go before the command. Examples: inside_ctl.py ctl run right 1.2 | inside_ctl.py \"srec record status\" | "
                                       "inside_ctl.py wait --timeout 90 | inside_ctl.py snapshot | inside_ctl.py doctor. "
                                       "Exit 0 ok, 1 failed, 2 not reachable, 3 refused.")
    p.add_argument("--port", type=int, default=None, help=f"the game's port (env {IL.ENV_PORT}, else {IL.DEFAULT_PORT})")
    p.add_argument("--host", default=IL.DEFAULT_HOST)
    p.add_argument("--token", default=None, help=f"bearer token of the game's mcp.token (env {IL.ENV_TOKEN})")
    p.add_argument("--game", type=Path, default=None, help=f"the INSIDE folder; the file bridge is <game>/_mod/cmd (env {IL.ENV_GAME})")
    p.add_argument("--bridge", type=Path, default=None, help=f"the file bridge folder <game>/_mod/cmd (env {IL.ENV_BRIDGE}; wins over --game)")
    g = p.add_mutually_exclusive_group()
    g.add_argument("--http-only", action="store_true", help="never use the file bridge")
    g.add_argument("--bridge-only", action="store_true", help="never use HTTP")
    p.add_argument("--force-bridge", action="store_true", help=f"write the bridge command also when no {IL.GAME_EXE} process is seen")
    p.add_argument("--no-wait", action="store_true", help="do not follow a started ctl job")
    p.add_argument("--timeout", type=float, default=None, help="seconds to follow a job, or to wait (120)")
    p.add_argument("--http-timeout", type=float, default=70.0, help="seconds to wait for one HTTP reply (70)")
    p.add_argument("--bridge-timeout", type=float, default=20.0, help="seconds to wait for the file bridge to take a command (20)")
    p.add_argument("--json", action="store_true", help="one JSON line per command: ok, text, error, via, job, exit")
    p.add_argument("--stop-on-error", action="store_true", help="repl / -f: stop at the first command that does not exit 0")
    p.add_argument("--quote-all", action="store_true", help="quote every argument that has a blank, also a single one")
    p.add_argument("-f", "--file", default=None, help="run the lines of this file as commands (- = standard input)")
    p.add_argument("words", nargs=argparse.REMAINDER, help="the command")
    a = p.parse_args(argv)
    if not a.words and a.file is None:
        p.error("give a command, state, snapshot, wait, doctor, seq \"<script>\", ping, repl or -f FILE")
    if a.words and a.file is not None:
        p.error("-f FILE takes no command")
    try:
        if a.port is None:
            a.port = IL.env_port()
    except ValueError as e:
        p.error(str(e))
    return a


def command_line(words: list[str], quote_all: bool) -> str:
    if len(words) == 1 and not quote_all:
        return words[0].strip()
    return quote_words(words)


def dispatch(a: argparse.Namespace, link: Link, out: Out) -> int:
    wait = not a.no_wait
    timeout = 120.0 if a.timeout is None else a.timeout
    if a.file is not None:
        return from_file(link, a.file, timeout, wait, out, a.stop_on_error)
    head, rest = a.words[0].lower(), a.words[1:]
    if head == "repl" and not rest:
        return repl(link, timeout, wait, out, a.stop_on_error)
    if head == "state" and not rest:
        r = link.state()
        code = out.reply("state", r)
        if r.failed and not r.text.lstrip().startswith("{"):
            print("the observe JSON needs InsideDev.Play.dll; the text snapshot of the bridge is: inside_ctl.py snapshot", file=sys.stderr)
        return code
    if head == "snapshot" and not rest:
        return out.reply("state", link.snapshot())
    if head == "ping" and not rest:
        r = link.get("/ping", "ping")
        if r.ok and not r.text.lstrip().lower().startswith("pong"):
            r.error = IL.classify(r.text, True) or "bad_reply"
        return out.reply("ping", r)
    if head == "wait":
        return wait_for(link, rest, timeout, out)
    if head == "doctor" and len(rest) <= 1:
        return doctor(link, rest, out)
    if head == "seq":
        if not rest:
            return out.problem("seq", "usage", 'inside_ctl.py seq "<script>"  (e.g. "R1.2 J R0.4+J G1 W0.5 L0.3")', EXIT_FAILED)
        return run_one(link, quote_words(["ctl", "seq", " ".join(rest)]), timeout, wait, out)
    if head == "cmd":
        if not rest:
            return out.problem("cmd", "usage", "inside_ctl.py cmd <line ...>", EXIT_FAILED)
        return run_one(link, command_line(rest, a.quote_all), timeout, wait, out)
    return run_one(link, command_line(a.words, a.quote_all), timeout, wait, out)


def main(argv: list[str]) -> int:
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass
    a = parse(argv)
    out = Out(a.json)
    link = Link(host=a.host, port=a.port, token=a.token, bridge=a.bridge, game=a.game, use_http=not a.bridge_only,
                use_bridge=not a.http_only, http_timeout=a.http_timeout, bridge_timeout=a.bridge_timeout, force_bridge=a.force_bridge,
                user_agent="inside_ctl")
    what = " ".join(a.words) if a.words else f"-f {a.file}"
    try:
        return dispatch(a, link, out)
    except Unreachable as e:
        return out.problem(what, "unreachable", str(e), EXIT_UNREACHABLE)
    except Lost as e:
        return out.problem(what, "lost", str(e), EXIT_UNREACHABLE)
    except KeyboardInterrupt:
        return out.problem(what, "interrupted", "stopped by Ctrl+C; a command already sent may still run", EXIT_INTERRUPTED)
    finally:
        link.close()


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
