#!/usr/bin/env python3
"""inside_link.py: the one client module of the Python tools for the running game (InsideDev). Standard library only,
Python 3.9+. Import it (sys.path must hold the tools folder); it does nothing on import.

API (stable: other tools import these names)

  Link(host=None, port=None, token=None, bridge=None, game=None, use_http=True, use_bridge=True,
       http_timeout=70.0, bridge_timeout=20.0, force_bridge=False)
      The connection to the game. Defaults: host 127.0.0.1; port from env INSIDE_MCP_PORT, else 47811; token from env
      INSIDEDEV_MCP_TOKEN (sent as "Authorization: Bearer <token>"); bridge folder from env INSIDE_BRIDGE, else
      <env INSIDE_GAME>/_mod/cmd, else the folder of a running INSIDE.exe (Windows). HTTP uses one keep-alive connection
      (http.client: never a system proxy); a Link may be shared between threads (one request at a time).
    .send(command, timeout=None) -> Reply    one console / bridge command: POST /cmd, or the file bridge when nothing
                                             listens (or the game's InsideDev.Mcp.dll has no /cmd)
    .run(command, wait=True, timeout=120, show=None) -> Reply    send, then follow "started: ... job=jN" with ctl job jN
    .get(path, bridge_command) -> Reply      GET a plain endpoint (/ping, /state); bridge_command is the fallback
    .ping(timeout=2.0) -> str | None         "pong InsideDev <version>" or None (never raises)
    .state() -> Reply                        the observe JSON (GET /state; bridge: ctl status json)
    .snapshot() -> Reply                     the bridge state text (command "state")
    .wait_ready(timeout=60.0, world=True) -> Ready    see wait_ready below
    .request(method, path, body=None, headers=None, timeout=None) -> HttpReply    raw HTTP on the kept connection
    .close()                                 drop the connection (also: with Link() as link: ...)
  Reply           .text .error (None = ok, else the server's code: bad_args, busy, refused, game_error, "http 401" ...)
                  .via ("http" | "bridge") .status .first (first line) .failed .refused .ok .exit_code (0 / 1 / 3)
  HttpReply       .status .headers (lower-case names) .body (bytes) .text
  Ready           truthy when the game is ready; .text (what answered or why not) .seconds .via
  wait_ready(timeout=60.0, link=None, world=True) -> Ready
      Polls until the game answers a ping and, with world=True, has a loaded world (GET /state answers 200; without
      InsideDev.Play: the "state" text has a character). world=False: the ping alone.
  McpClient(link=None, profile="dev", name="inside_link", version="1", timeout=70.0)
      MCP JSON-RPC client over the Streamable HTTP transport (profile dev = /mcp, play = /mcp/play, all = /mcp/all).
    .initialize() -> dict                    protocolVersion, serverInfo, instructions; keeps the Mcp-Session-Id
    .list_tools() -> list[dict]
    .call(tool, arguments=None, timeout=None, progress=None) -> ToolResult
                                             progress(progress, total, message): asks for an SSE-framed reply
    .request(method, params=None, timeout=None) -> the JSON-RPC result;  .notify(method, params=None);  .close()
  ToolResult      .ok .text .structured .content .error ({code, message, hint} or None) .error_code .raw
  sse_events(lines) -> iterator of (event, data)    Server-Sent Events framing of an iterable of byte / str lines
  classify(text, bridge=False) -> str | None        the server's error word list (src/mcp/HostText.cs Classify) on the
                                                    first line of a reply; None = ok
  quote(word) -> str, quote_words(words) -> str     console quoting (the game groups "..." and has no escape)
  job_of(reply) -> str | None                       the job id of a "started: ... job=jN" reply
  processes(names) -> {lower name: [pid]} | None    running processes by image name (None: cannot tell here)
  process_path(pid) -> str | None, game_dir() -> Path | None, port_open(host, port, timeout=1.0) -> bool,
  windows_of(pids) -> list[dict(pid, title, visible, minimized)] | None
  local_checks(link) -> list[(status, name, detail)]    what can be said about the game from outside (doctor)
  Exceptions: LinkError > Unreachable (nothing was sent: no game), Lost (sent, no reply: the command may have run);
              McpError(code, message, data) (a JSON-RPC error; a tool that fails is a ToolResult with ok False)
  Exit codes: EXIT_OK 0, EXIT_FAILED 1, EXIT_UNREACHABLE 2, EXIT_REFUSED 3
"""
from __future__ import annotations

import http.client
import json
import os
import re
import select
import socket
import threading
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Iterable, Iterator

__all__ = ["Link", "Reply", "HttpReply", "Ready", "McpClient", "ToolResult", "LinkError", "Unreachable", "Lost", "McpError",
           "wait_ready", "sse_events", "classify", "quote", "quote_words", "job_of", "processes", "process_path", "game_dir",
           "port_open", "windows_of", "local_checks", "env_port", "DEFAULT_HOST", "DEFAULT_PORT", "EXIT_OK", "EXIT_FAILED",
           "EXIT_UNREACHABLE", "EXIT_REFUSED", "GAME_EXE"]

DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 47811
ENV_PORT, ENV_TOKEN, ENV_GAME, ENV_BRIDGE = "INSIDE_MCP_PORT", "INSIDEDEV_MCP_TOKEN", "INSIDE_GAME", "INSIDE_BRIDGE"
EXIT_OK, EXIT_FAILED, EXIT_UNREACHABLE, EXIT_REFUSED = 0, 1, 2, 3
GAME_EXE = "INSIDE.exe"
STEAM_EXE = "steam.exe"
ERROR_HEADER = "x-insidedev-error"
TRUNCATED_HEADER = "x-insidedev-truncated"
MCP_VERSIONS = ("2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05")
JOB_RE = re.compile(r"\bjob=(j\d+)\b")
REFUSED_CODES = ("refused", "unauthorized", "unsafe", "confirm_required", "http 401", "http 403")
IDLE_RECONNECT_S = 60.0
CONNECT_TIMEOUT_S = 5.0
LOCAL_CONNECT_TIMEOUT_S = 1.0
HTTP_RETRY_S = 5.0
NO_PLAIN_RETRY_S = 60.0

# The tables of HostText.Classify (src/mcp/HostText.cs). They are read from that file when it sits next to the tools
# (error_tables), so the copy below is only the fallback for a tools folder without the sources.
_STARTS = (("not found", "not_found"), ("no match", "not_found"), ("usage:", "bad_args"), ("usage ", "bad_args"), ("unknown ", "bad_args"),
           ("unknown:", "bad_args"), ("busy:", "busy"), ("busy ", "busy"), ("no fsm", "not_found"), ("refused", "refused"),
           ("failed:", "game_error"), ("failed to ", "game_error"), ("error:", "game_error"), ("error in ", "game_error"),
           ("error -", "game_error"), ("error ", "game_error"), ("cannot ", "game_error"), ("can't ", "game_error"),
           ("could not ", "game_error"), ("couldn't ", "game_error"), ("no active ", "not_ready"), ("not built", "not_built"),
           ("object scan not finished", "not_ready"))
_CONTAINS = (("component not found", "not_found"), ("field/property not found", "not_found"), ("no parameterless method", "not_found"),
             ("no active character", "not_ready"), (" failed:", "game_error"), ("not built", "not_built"), ("not ready", "not_ready"))
_EXACT = {"not found": "not_found", "busy": "busy", "unknown": "bad_args", "error": "game_error", "failed": "game_error"}
_BRIDGE_STARTS = (("error ", "game_error"),)
_tables: tuple | None = None


class LinkError(Exception):
    pass


class Unreachable(LinkError):
    pass


class Lost(LinkError):
    pass


class McpError(LinkError):
    def __init__(self, code: int, message: str, data: Any = None):
        super().__init__(f"{message} (JSON-RPC {code})")
        self.code, self.message, self.data = code, message, data


def env_port(default: int = DEFAULT_PORT) -> int:
    raw = (os.environ.get(ENV_PORT) or "").strip()
    if not raw:
        return default
    try:
        port = int(raw)
    except ValueError:
        raise ValueError(f"{ENV_PORT} must be a port number (got '{raw}')") from None
    if not 0 < port < 65536:
        raise ValueError(f"{ENV_PORT} must be 1..65535 (got {port})")
    return port


def _camel_code(name: str) -> str:
    return re.sub(r"(?<!^)([A-Z])", r"_\1", name).lower()


def error_tables() -> tuple:
    global _tables
    if _tables is not None:
        return _tables
    starts, contains = _STARTS, _CONTAINS
    src = Path(__file__).resolve().parent.parent / "src" / "mcp"
    try:
        text = (src / "HostText.cs").read_text(encoding="utf-8-sig")
        codes: dict[str, str] = {}
        try:
            api = (src / "ServerApi.cs").read_text(encoding="utf-8-sig")
            m = re.search(r"class\s+McpErr\s*\{(.*?)\}", api, re.S)
            codes = dict(re.findall(r'(\w+)\s*=\s*"([^"]*)"', m.group(1))) if m else {}
        except OSError:
            pass
        found = []
        for name in ("Starts", "Contains"):
            m = re.search(r"static\s+readonly\s+string\[\]\[\]\s+" + name + r"\s*=\s*\{(.*?)\};", text, re.S)
            rows = re.findall(r'new\[\]\s*\{\s*"((?:[^"\\]|\\.)*)"\s*,\s*McpErr\.(\w+)\s*\}', m.group(1)) if m else []
            found.append(tuple((json.loads(f'"{word}"'), codes.get(code) or _camel_code(code)) for word, code in rows))
        if found[0] and found[1]:
            starts, contains = found
    except (OSError, ValueError):
        pass
    _tables = (starts, contains)
    return _tables


def first_line(text: str) -> str:
    for raw in (text or "").split("\n"):
        line = raw.strip()
        if line:
            return line
    return ""


def classify(text: str, bridge: bool = False) -> str | None:
    first = first_line(text)
    if not first:
        return None
    if "ERROR in " in first:
        return "game_error"
    low = first.lower()
    if low in _EXACT:
        return _EXACT[low]
    starts, contains = error_tables()
    for word, code in starts:
        if low.startswith(word):
            return code
    for word, code in contains:
        if word in low:
            return code
    if low.endswith(" failed"):
        return "game_error"
    if bridge:
        for word, code in _BRIDGE_STARTS:
            if low.startswith(word):
                return code
    return None


def quote(word: str) -> str:
    return '"' + str(word).replace('"', "") + '"'


def quote_words(words: Iterable[str]) -> str:
    out = []
    for w in words:
        w = str(w).replace('"', "")
        out.append(f'"{w}"' if (" " in w or "\t" in w) else w)
    return " ".join(out)


@dataclass
class HttpReply:
    status: int
    headers: dict
    body: bytes

    @property
    def text(self) -> str:
        return self.body.decode("utf-8", "replace")


@dataclass
class Reply:
    text: str
    error: str | None
    via: str
    status: int = 200
    truncated: bool = False

    @property
    def first(self) -> str:
        return self.text.split("\n", 1)[0] if self.text else ""

    @property
    def failed(self) -> bool:
        return self.error is not None

    @property
    def ok(self) -> bool:
        return self.error is None

    @property
    def refused(self) -> bool:
        return self.error in REFUSED_CODES

    @property
    def exit_code(self) -> int:
        return EXIT_OK if self.error is None else EXIT_REFUSED if self.refused else EXIT_FAILED

    def _classified(self) -> "Reply":
        self.error = classify(self.text, True)
        return self


def _bridge_text(lines: list) -> str:
    return "\n".join(ln[len("  log: "):] if ln.startswith("  log: ") else ln for ln in lines).rstrip("\n")


@dataclass
class Ready:
    ok: bool
    text: str
    seconds: float
    via: str = ""

    def __bool__(self) -> bool:
        return self.ok


@dataclass
class ToolResult:
    ok: bool
    text: str
    structured: dict | None
    content: list
    error: dict | None
    raw: dict = field(default_factory=dict)

    @property
    def error_code(self) -> str | None:
        return None if self.ok else str((self.error or {}).get("code") or "error")


def job_of(reply: Reply) -> str | None:
    if reply.failed or not reply.first.startswith("started:"):
        return None
    m = JOB_RE.search(reply.first)
    return m.group(1) if m else None


def sse_events(lines: Iterable) -> Iterator[tuple[str, str]]:
    name, data = "", []
    for raw in lines:
        line = raw.decode("utf-8", "replace") if isinstance(raw, (bytes, bytearray)) else raw
        line = line.rstrip("\n").rstrip("\r")
        if not line:
            if data:
                yield name or "message", "\n".join(data)
            name, data = "", []
            continue
        if line.startswith(":"):
            continue
        key, colon, value = line.partition(":")
        if colon and value.startswith(" "):
            value = value[1:]
        if key == "data":
            data.append(value)
        elif key == "event":
            name = value
    if data:
        yield name or "message", "\n".join(data)


def _connect(host: str, port: int) -> http.client.HTTPConnection:
    local = host in ("localhost", "::1") or host.startswith("127.")
    conn = http.client.HTTPConnection(host, port, timeout=LOCAL_CONNECT_TIMEOUT_S if local else CONNECT_TIMEOUT_S)
    try:
        conn.connect()
    except OSError as e:
        conn.close()
        raise Unreachable(f"nothing listens on http://{host}:{port} ({type(e).__name__}: {e})") from e
    return conn


class Link:
    def __init__(self, host: str | None = None, port: int | None = None, token: str | None = None, bridge: Path | str | None = None,
                 game: Path | str | None = None, use_http: bool = True, use_bridge: bool = True, http_timeout: float = 70.0,
                 bridge_timeout: float = 20.0, force_bridge: bool = False, user_agent: str = "inside_link"):
        self.host = host or DEFAULT_HOST
        self.port = int(port) if port else env_port()
        self.token = token if token is not None else os.environ.get(ENV_TOKEN, "")
        if bridge is None and os.environ.get(ENV_BRIDGE):
            bridge = os.environ[ENV_BRIDGE]
        if bridge is None and game is None and os.environ.get(ENV_GAME):
            game = os.environ[ENV_GAME]
        self.bridge: Path | None = Path(bridge) if bridge is not None else (Path(game) / "_mod" / "cmd" if game is not None else None)
        self.bridge_found = False
        self.use_http, self.use_bridge = use_http, use_bridge
        self.http_timeout, self.bridge_timeout = float(http_timeout), float(bridge_timeout)
        self.force_bridge = force_bridge
        self.user_agent = user_agent
        self.calls = 0
        self._conn: http.client.HTTPConnection | None = None
        self._used = 0.0
        self._lock = threading.RLock()
        self._http_retry_at = 0.0
        self._seq = 0

    def __enter__(self) -> "Link":
        return self

    def __exit__(self, *exc) -> None:
        self.close()

    @property
    def url(self) -> str:
        return f"http://{self.host}:{self.port}"

    @property
    def http_down(self) -> bool:
        return time.monotonic() < self._http_retry_at

    def close(self) -> None:
        with self._lock:
            conn, self._conn = self._conn, None
        if conn is not None:
            try:
                conn.close()
            except OSError:
                pass

    def _connection(self) -> tuple[http.client.HTTPConnection, bool]:
        conn = self._conn
        if conn is not None:
            stale = conn.sock is None or time.monotonic() - self._used > IDLE_RECONNECT_S
            if not stale:
                try:
                    stale = bool(select.select([conn.sock], [], [], 0)[0])
                except (OSError, ValueError):
                    stale = True
            if not stale:
                return conn, False
            self.close()
        conn = _connect(self.host, self.port)
        self._conn = conn
        return conn, True

    def request(self, method: str, path: str, body: str | bytes | None = None, headers: dict | None = None,
                timeout: float | None = None) -> HttpReply:
        data = body.encode("utf-8") if isinstance(body, str) else body
        hdrs = {"User-Agent": self.user_agent, "Accept": "*/*"}
        if data is not None:
            hdrs["Content-Type"] = "text/plain; charset=utf-8"
        if self.token:
            hdrs["Authorization"] = "Bearer " + self.token
        hdrs.update(headers or {})
        limit = self.http_timeout if timeout is None else float(timeout)
        with self._lock:
            self.calls += 1
            for attempt in (0, 1):
                conn, fresh = self._connection()
                try:
                    conn.sock.settimeout(limit)
                    conn.request(method, path, body=data, headers=hdrs)
                    resp = conn.getresponse()
                    raw = resp.read()
                    out = HttpReply(resp.status, {k.lower(): v for k, v in resp.getheaders()}, raw)
                    self._used = time.monotonic()
                    if resp.will_close:
                        self.close()
                    return out
                except (OSError, http.client.HTTPException) as e:
                    self.close()
                    dropped = isinstance(e, (http.client.RemoteDisconnected, ConnectionResetError, BrokenPipeError, ConnectionAbortedError))
                    if attempt == 0 and not fresh and dropped and method in ("GET", "HEAD"):
                        continue
                    why = f"no reply in {limit:.0f} s" if isinstance(e, socket.timeout) else f"{type(e).__name__}: {e}"
                    raise Lost(f"{self.url}{path}: {why}; the request may have run") from e
        raise Lost(f"{self.url}{path}: no reply")

    def _plain(self, method: str, path: str, body: str | None, timeout: float | None, fallback: str) -> Reply:
        if self.use_http and not self.http_down:
            try:
                r = self.request(method, path, body, timeout=timeout)
            except Unreachable:
                self._http_retry_at = time.monotonic() + HTTP_RETRY_S
                if not self.use_bridge:
                    raise
            else:
                text = r.text.rstrip("\n")
                if r.status == 200:
                    code = r.headers.get(ERROR_HEADER) or (classify(text) if method == "POST" else None)
                    return Reply(text, code, "http", 200, TRUNCATED_HEADER in r.headers)
                if r.status != 404:
                    return Reply(text, f"http {r.status}", "http", r.status)
                self._http_retry_at = time.monotonic() + NO_PLAIN_RETRY_S
                if not self.use_bridge:
                    raise Unreachable(f"{self.url} does not serve {path} (an older InsideDev.Mcp.dll) and the bridge is off")
        if not self.use_bridge:
            raise Unreachable(f"{self.url} is not reachable and the bridge is off")
        return self.via_bridge(fallback, timeout)

    def send(self, command: str, timeout: float | None = None) -> Reply:
        return self._plain("POST", "/cmd", command, timeout, command)

    def get(self, path: str, bridge_command: str, timeout: float | None = None) -> Reply:
        return self._plain("GET", path, None, timeout, bridge_command)

    def state(self) -> Reply:
        r = self.get("/state", "ctl status json")
        if r.ok and not r.text.lstrip().startswith("{"):
            r.error = classify(r.text, True) or "bad_reply"
        return r

    def snapshot(self) -> Reply:
        return self.send("state")

    def ping(self, timeout: float = 2.0) -> str | None:
        try:
            r = self.get("/ping", "ping", timeout=timeout)
        except LinkError:
            return None
        return r.text.strip() if r.ok and r.text.lstrip().lower().startswith("pong") else None

    def run(self, command: str, wait: bool = True, timeout: float = 120.0, show: Callable[[str], None] | None = None) -> Reply:
        reply = self.send(command)
        job = job_of(reply) if wait else None
        if job is None:
            return reply
        if show is not None:
            show(reply.first)
        deadline = time.monotonic() + timeout
        pause = 0.1 if reply.via == "http" else 0.4
        try:
            while True:
                time.sleep(pause)
                r = self.send(f"ctl job {job}")
                if not r.first.startswith("running:"):
                    return r
                if time.monotonic() > deadline:
                    return Reply(r.text + f"\n(still running after {timeout:.0f} s; ctl job {job} follows it, ctl stop cancels it)", "timeout", r.via)
        except KeyboardInterrupt:
            r = self.send("ctl stop")
            return Reply(r.text, "interrupted", r.via)

    def bridge_dir(self) -> Path | None:
        if self.bridge is None:
            found = game_dir()
            if found is not None:
                self.bridge, self.bridge_found = found / "_mod" / "cmd", True
        return self.bridge

    def via_bridge(self, command: str, timeout: float | None = None) -> Reply:
        d = self.bridge_dir()
        if d is None:
            raise Unreachable(f"no game: {self.url} is down and no bridge folder is known (--game DIR or --bridge DIR, env {ENV_GAME} / "
                              f"{ENV_BRIDGE}; no running {GAME_EXE} found)")
        if not d.is_dir():
            raise Unreachable(f"no game: {self.url} is down and the bridge folder {d} does not exist")
        if not self.force_bridge:
            running = processes([GAME_EXE])
            if running is not None and not running.get(GAME_EXE.lower()):
                raise Unreachable(f"no game: {self.url} is down and {GAME_EXE} is not running (the bridge {d} would not answer)")
        limit = self.bridge_timeout if timeout is None else float(timeout)
        out = d / "out.txt"
        try:
            start = out.stat().st_size
        except OSError:
            start = 0
        with self._lock:
            self._seq += 1
            self.calls += 1
            rid = f"c{os.getpid() % 1000:03d}{(time.time_ns() // 1_000_000 + self._seq) % 1_000_000:06d}"
        tmp, req, own = d / f"ctl_{rid}.tmp", d / f"in_{rid}.txt", d / f"out_{rid}.txt"
        head, tail = f">>> {rid}  ", f"\n<<< {rid}"
        try:
            with tmp.open("w", encoding="utf-8", newline="\n") as f:
                f.write(f"{rid}|{command}\n")
            os.replace(tmp, req)
            t0 = time.monotonic()
            pickup = min(limit, self.bridge_timeout)
            taken = False
            while time.monotonic() - t0 < limit:
                time.sleep(0.05)
                if not taken:
                    taken = not req.exists()
                    if not taken and time.monotonic() - t0 >= pickup:
                        break
                reply = self._own_reply(own)
                if reply is not None:
                    return reply
                try:
                    size = out.stat().st_size
                    if size < start:
                        start = 0
                    if size == start:
                        continue
                    with out.open("rb") as f:
                        f.seek(start)
                        chunk = f.read().decode("utf-8", "replace")
                except OSError:
                    continue
                i = chunk.find(head)
                j = chunk.find(tail, i) if i >= 0 else -1
                if j < 0:
                    continue
                reply = self._own_reply(own)
                if reply is not None:
                    return reply
                return Reply(_bridge_text(chunk[i:j].split("\n")[1:]), None, "bridge")._classified()
            if req.exists():
                raise Unreachable(f"the bridge {d} did not pick the command up in {pickup:.0f} s{self.stale_note()}")
            raise Lost(f"the bridge took the command but wrote no reply in {limit:.0f} s; it may have run")
        finally:
            for p in (tmp, req, own):
                try:
                    p.unlink()
                except OSError:
                    pass

    @staticmethod
    def _own_reply(path: Path) -> Reply | None:
        """The reply file out_<id>.txt of a 0.11.0 game: "status: ok | error", more "key: value" lines, an empty line, the reply."""
        try:
            raw = path.read_text(encoding="utf-8-sig", errors="replace")
        except OSError:
            return None
        head, sep, body = raw.replace("\r\n", "\n").partition("\n\n")
        if not sep or not head.startswith("status:"):
            return None
        reply = Reply(_bridge_text(body.split("\n")), None, "bridge")._classified()
        if head.split("\n", 1)[0].split(":", 1)[1].strip() == "error" and reply.error is None:
            reply.error = "game_error"
        return reply

    def stale_note(self) -> str:
        if self.bridge is None:
            return ""
        try:
            age = time.time() - (self.bridge / "state.txt").stat().st_mtime
        except OSError:
            return " (no state.txt: the game does not seem to run with InsideDev)"
        return f" (state.txt is {age:.0f} s old: the game is paused, minimized, loading or not running)" if age > 15 else ""

    def wait_ready(self, timeout: float = 60.0, world: bool = True) -> Ready:
        return wait_ready(timeout, self, world)


def _world_state(link: Link) -> tuple[bool, str]:
    r = link.get("/state", "ctl status json", timeout=10.0)
    if r.status == 200 and r.via == "http":
        return True, "the world is loaded (GET /state)"
    body = r.text.strip()
    if r.via == "http" and r.status == 503 and body.startswith("{"):
        try:
            err = (json.loads(body).get("error") or {})
            return False, str(err.get("message") or err.get("code") or "no world yet")
        except ValueError:
            return False, "no world yet"
    if r.via == "bridge" and r.ok and body.startswith("{"):
        try:
            doc = json.loads(body)
            if doc.get("ok") is False:
                return False, str((doc.get("error") or {}).get("message") or "no world yet")
            return True, "the world is loaded (ctl status)"
        except ValueError:
            pass
    if r.status in (401, 403) and "allowlist" not in body:
        return False, first_line(body) or f"http {r.status}"
    s = link.snapshot()
    for line in s.text.split("\n"):
        if line.startswith("character:"):
            who = line[len("character:"):].strip()
            return (not who.startswith("none")), ("the world is loaded (" + line.strip() + ")" if not who.startswith("none") else "no character yet")
    return False, first_line(s.text) or "no state"


def wait_ready(timeout: float = 60.0, link: Link | None = None, world: bool = True) -> Ready:
    own = link is None
    link = link or Link()
    t0 = time.monotonic()
    why, via = "no answer", ""
    keep = link.bridge_timeout
    link.bridge_timeout = min(keep, 4.0)
    try:
        while True:
            try:
                r = link.get("/ping", "ping", timeout=3.5)
                via = r.via
                if r.ok and r.text.lstrip().lower().startswith("pong"):
                    if not world:
                        return Ready(True, r.text.strip(), time.monotonic() - t0, via)
                    ok, why = _world_state(link)
                    if ok:
                        return Ready(True, r.text.strip() + ": " + why, time.monotonic() - t0, via)
                else:
                    why = first_line(r.text) or (r.error or "no pong")
            except LinkError as e:
                why = str(e)
            if time.monotonic() - t0 >= timeout:
                return Ready(False, why, time.monotonic() - t0, via)
            time.sleep(0.5)
    finally:
        link.bridge_timeout = keep
        if own:
            link.close()


class McpClient:
    PATHS = {"dev": "/mcp", "play": "/mcp/play", "all": "/mcp/all"}

    def __init__(self, link: Link | None = None, profile: str = "dev", name: str = "inside_link", version: str = "1",
                 timeout: float = 70.0):
        if profile not in self.PATHS:
            raise ValueError(f"profile must be one of {', '.join(self.PATHS)}")
        self.link = link or Link()
        self.own_link = link is None
        self.path = self.PATHS[profile]
        self.name, self.version, self.timeout = name, version, float(timeout)
        self.session: str | None = None
        self.protocol: str | None = None
        self.server: dict = {}
        self.instructions = ""
        self._id = 0
        self._lock = threading.Lock()

    def __enter__(self) -> "McpClient":
        return self

    def __exit__(self, *exc) -> None:
        self.close()

    def _headers(self, stream: bool) -> dict:
        h = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream" if stream else "application/json"}
        if self.session:
            h["Mcp-Session-Id"] = self.session
        if self.protocol:
            h["MCP-Protocol-Version"] = self.protocol
        return h

    def _next_id(self) -> int:
        with self._lock:
            self._id += 1
            return self._id

    @staticmethod
    def _message(reply: HttpReply, want_id: Any) -> dict | None:
        ctype = reply.headers.get("content-type", "")
        docs = []
        if "text/event-stream" in ctype:
            docs = [json.loads(data) for _, data in sse_events(reply.body.split(b"\n")) if data.strip()]
        elif reply.body.strip():
            doc = json.loads(reply.body.decode("utf-8", "replace"))
            docs = doc if isinstance(doc, list) else [doc]
        for doc in docs:
            if isinstance(doc, dict) and ("result" in doc or "error" in doc) and (want_id is None or doc.get("id") == want_id):
                return doc
        return None

    @staticmethod
    def _result(doc: dict | None, what: str) -> Any:
        if doc is None:
            raise McpError(-32603, f"{what}: no JSON-RPC response in the reply")
        if doc.get("error") is not None:
            e = doc["error"] if isinstance(doc["error"], dict) else {"message": str(doc["error"])}
            raise McpError(int(e.get("code") or -32603), str(e.get("message") or "error"), e.get("data"))
        return doc.get("result")

    def _post(self, payload: dict, timeout: float | None, stream: bool = False) -> HttpReply:
        r = self.link.request("POST", self.path, json.dumps(payload), self._headers(stream), self.timeout if timeout is None else timeout)
        if r.status in (401, 403):
            raise McpError(-32001, first_line(r.text) or f"http {r.status}", {"http": r.status})
        return r

    def initialize(self) -> dict:
        self.session = self.protocol = None
        rid = self._next_id()
        r = self._post({"jsonrpc": "2.0", "id": rid, "method": "initialize",
                        "params": {"protocolVersion": MCP_VERSIONS[0], "capabilities": {},
                                   "clientInfo": {"name": self.name, "version": self.version}}}, None)
        if r.status != 200:
            raise McpError(-32603, f"initialize: http {r.status}: {first_line(r.text)}", {"http": r.status})
        res = self._result(self._message(r, rid), "initialize") or {}
        self.session = r.headers.get("mcp-session-id") or None
        self.protocol = str(res.get("protocolVersion") or MCP_VERSIONS[0])
        self.server = res.get("serverInfo") or {}
        self.instructions = str(res.get("instructions") or "")
        self.notify("notifications/initialized")
        return res

    def notify(self, method: str, params: dict | None = None) -> None:
        msg: dict = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            msg["params"] = params
        self._post(msg, None)

    def request(self, method: str, params: dict | None = None, timeout: float | None = None) -> Any:
        if self.session is None and method != "initialize":
            self.initialize()
        for attempt in (0, 1):
            rid = self._next_id()
            msg: dict = {"jsonrpc": "2.0", "id": rid, "method": method}
            if params is not None:
                msg["params"] = params
            r = self._post(msg, timeout)
            if r.status == 404 and attempt == 0 and self.session is not None:
                self.initialize()
                continue
            if r.status not in (200, 400):
                raise McpError(-32603, f"{method}: http {r.status}: {first_line(r.text)}", {"http": r.status})
            return self._result(self._message(r, rid), method)
        raise McpError(-32603, f"{method}: the session was not accepted")

    def list_tools(self) -> list:
        return list((self.request("tools/list") or {}).get("tools") or [])

    @staticmethod
    def _tool_result(res: dict | None) -> ToolResult:
        res = res if isinstance(res, dict) else {}
        content = res.get("content") if isinstance(res.get("content"), list) else []
        text = "\n".join(str(c.get("text", "")) for c in content if isinstance(c, dict) and c.get("type") == "text")
        structured = res.get("structuredContent") if isinstance(res.get("structuredContent"), dict) else None
        failed = bool(res.get("isError"))
        error = None
        if failed:
            e = (structured or {}).get("error")
            error = e if isinstance(e, dict) else {"code": "error", "message": first_line(text)}
        return ToolResult(not failed, text, structured, content, error, res)

    def call(self, tool: str, arguments: dict | None = None, timeout: float | None = None,
             progress: Callable[[float, float, str], None] | None = None) -> ToolResult:
        params: dict = {"name": tool, "arguments": arguments or {}}
        if progress is None:
            return self._tool_result(self.request("tools/call", params, timeout))
        if self.session is None:
            self.initialize()
        rid = self._next_id()
        params["_meta"] = {"progressToken": f"p{rid}"}
        body = json.dumps({"jsonrpc": "2.0", "id": rid, "method": "tools/call", "params": params}).encode("utf-8")
        hdrs = self._headers(True)
        hdrs["User-Agent"] = self.link.user_agent
        if self.link.token:
            hdrs["Authorization"] = "Bearer " + self.link.token
        limit = self.timeout if timeout is None else float(timeout)
        conn = _connect(self.link.host, self.link.port)
        try:
            try:
                conn.sock.settimeout(limit)
                conn.request("POST", self.path, body=body, headers=hdrs)
                resp = conn.getresponse()
                if "text/event-stream" not in (resp.getheader("Content-Type") or ""):
                    r = HttpReply(resp.status, {k.lower(): v for k, v in resp.getheaders()}, resp.read())
                    if r.status in (401, 403):
                        raise McpError(-32001, first_line(r.text) or f"http {r.status}", {"http": r.status})
                    if r.status not in (200, 400):
                        raise McpError(-32603, f"tools/call: http {r.status}: {first_line(r.text)}", {"http": r.status})
                    return self._tool_result(self._result(self._message(r, rid), "tools/call"))
                for _, data in sse_events(iter(resp.readline, b"")):
                    if not data.strip():
                        continue
                    doc = json.loads(data)
                    if not isinstance(doc, dict):
                        continue
                    if doc.get("method") == "notifications/progress":
                        p = doc.get("params") or {}
                        progress(float(p.get("progress") or 0), float(p.get("total") or 0), str(p.get("message") or ""))
                    elif ("result" in doc or "error" in doc) and doc.get("id") == rid:
                        return self._tool_result(self._result(doc, "tools/call"))
                raise Lost(f"{self.link.url}{self.path}: the stream of tools/call {tool} ended without a response; the call may have run")
            except (OSError, http.client.HTTPException, ValueError) as e:
                raise Lost(f"{self.link.url}{self.path}: tools/call {tool}: {type(e).__name__}: {e}; the call may have run") from e
        finally:
            conn.close()

    def close(self) -> None:
        if self.session is not None:
            try:
                self.link.request("DELETE", self.path, None, self._headers(False), 5.0)
            except LinkError:
                pass
            self.session = None
        if self.own_link:
            self.link.close()


# ---------------------------------------------------------------------------------------------------- local checks


def _win():
    import ctypes
    from ctypes import wintypes
    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    u32 = ctypes.WinDLL("user32", use_last_error=True)
    return ctypes, wintypes, k32, u32


def processes(names: Iterable[str]) -> dict | None:
    want = {n.lower() for n in names}
    out: dict = {n: [] for n in want}
    if os.name != "nt":
        return None
    try:
        ctypes, wt, k32, _ = _win()

        class Entry(ctypes.Structure):
            _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD), ("th32DefaultHeapID", ctypes.c_size_t),
                        ("th32ModuleID", wt.DWORD), ("cntThreads", wt.DWORD), ("th32ParentProcessID", wt.DWORD), ("pcPriClassBase", wt.LONG),
                        ("dwFlags", wt.DWORD), ("szExeFile", wt.WCHAR * 260)]

        k32.CreateToolhelp32Snapshot.restype = wt.HANDLE
        k32.CreateToolhelp32Snapshot.argtypes = [wt.DWORD, wt.DWORD]
        k32.Process32FirstW.argtypes = k32.Process32NextW.argtypes = [wt.HANDLE, ctypes.POINTER(Entry)]
        k32.CloseHandle.argtypes = [wt.HANDLE]
        snap = k32.CreateToolhelp32Snapshot(0x2, 0)
        if snap is None or snap == wt.HANDLE(-1).value:
            return None
        try:
            e = Entry()
            e.dwSize = ctypes.sizeof(Entry)
            more = k32.Process32FirstW(snap, ctypes.byref(e))
            while more:
                name = e.szExeFile.lower()
                if name in want:
                    out[name].append(int(e.th32ProcessID))
                more = k32.Process32NextW(snap, ctypes.byref(e))
        finally:
            k32.CloseHandle(snap)
        return out
    except (OSError, AttributeError, ValueError):
        return None


def process_path(pid: int) -> str | None:
    if os.name != "nt":
        return None
    try:
        ctypes, wt, k32, _ = _win()
        k32.OpenProcess.restype = wt.HANDLE
        k32.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
        k32.QueryFullProcessImageNameW.argtypes = [wt.HANDLE, wt.DWORD, wt.LPWSTR, ctypes.POINTER(wt.DWORD)]
        k32.CloseHandle.argtypes = [wt.HANDLE]
        h = k32.OpenProcess(0x1000, False, int(pid))
        if not h:
            return None
        try:
            buf = ctypes.create_unicode_buffer(32768)
            n = wt.DWORD(len(buf))
            return buf.value if k32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(n)) else None
        finally:
            k32.CloseHandle(h)
    except (OSError, AttributeError, ValueError):
        return None


def game_dir() -> Path | None:
    running = processes([GAME_EXE]) or {}
    for pid in running.get(GAME_EXE.lower(), []):
        p = process_path(pid)
        if p:
            return Path(p).parent
    return None


def windows_of(pids: Iterable[int]) -> list | None:
    want = {int(p) for p in pids}
    if os.name != "nt":
        return None
    try:
        ctypes, wt, _, u32 = _win()
        found: list = []
        proc_type = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
        u32.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]
        u32.GetWindowTextW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
        u32.IsWindowVisible.argtypes = u32.IsIconic.argtypes = [wt.HWND]
        u32.GetWindow.argtypes = [wt.HWND, wt.UINT]
        u32.GetWindow.restype = wt.HWND

        def each(hwnd, _lparam):
            pid = wt.DWORD(0)
            u32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
            if pid.value in want and not u32.GetWindow(hwnd, 4):
                buf = ctypes.create_unicode_buffer(256)
                u32.GetWindowTextW(hwnd, buf, 256)
                found.append({"pid": int(pid.value), "title": buf.value, "visible": bool(u32.IsWindowVisible(hwnd)),
                              "minimized": bool(u32.IsIconic(hwnd))})
            return True

        callback = proc_type(each)
        u32.EnumWindows.argtypes = [proc_type, wt.LPARAM]
        u32.EnumWindows(callback, 0)
        return found
    except (OSError, AttributeError, ValueError):
        return None


def port_open(host: str, port: int, timeout: float = 1.0) -> bool:
    try:
        with socket.create_connection((host, port), timeout=timeout):
            return True
    except OSError:
        return False


def local_checks(link: Link) -> list:
    rows: list = []
    running = processes([GAME_EXE, STEAM_EXE])
    game_pids = (running or {}).get(GAME_EXE.lower(), [])
    if running is None:
        rows.append(("info", "game process", f"cannot list processes on this system; look for {GAME_EXE} yourself"))
    elif game_pids:
        where = process_path(game_pids[0])
        rows.append(("ok", "game process", f"{GAME_EXE} is running (pid {', '.join(map(str, game_pids))}{', ' + where if where else ''})"))
    else:
        rows.append(("fail", "game process", f"{GAME_EXE} is not running: start the game from Steam"))
    if running is not None:
        if running.get(STEAM_EXE.lower()):
            rows.append(("ok", "Steam client", f"{STEAM_EXE} is running"))
        else:
            rows.append(("fail" if not game_pids else "warn", "Steam client",
                         f"{STEAM_EXE} is not running: without the Steam client the game exits two seconds after its start"))
    if link.use_http:
        if port_open(link.host, link.port):
            with Link(link.host, link.port, link.token, use_bridge=False, http_timeout=3.0) as probe:
                pong = probe.ping(2.0)
            if pong:
                rows.append(("ok", "port", f"{link.url} answers: {pong}"))
            else:
                rows.append(("fail", "port", f"{link.url} is open but does not answer GET /ping like InsideDev: another program uses the port "
                                             f"(set the game's port with 'mcp port <n>' and env {ENV_PORT})"))
        else:
            rows.append(("fail" if game_pids else "info", "port",
                         f"nothing listens on {link.url}" + (": InsideDev.Mcp.dll is not installed, 'mcp off' was given, or the game uses another "
                                                              f"port (console: mcp status; env {ENV_PORT})" if game_pids else "")))
    if game_pids:
        wins = windows_of(game_pids)
        main = [w for w in wins or [] if w["visible"] and w["title"]]
        if wins is None:
            rows.append(("info", "game window", "cannot look at windows on this system"))
        elif not main:
            rows.append(("warn", "game window", "no visible game window yet (the game is still starting, or it runs on another desktop)"))
        elif any(w["minimized"] for w in main):
            rows.append(("fail", "game window", "the game window is minimized: the game is paused and answers nothing; restore the window"))
        else:
            rows.append(("ok", "game window", f"'{main[0]['title']}' is open and not minimized"))
    d = link.bridge_dir() if link.use_bridge else None
    if link.use_bridge:
        if d is None:
            rows.append(("info", "file bridge", f"no bridge folder known: give --game DIR (or env {ENV_GAME}); it is <game>/_mod/cmd"))
        elif not d.is_dir():
            rows.append(("fail", "file bridge", f"{d} does not exist: InsideDev has not run in that game folder (is version.dll installed there?)"))
        else:
            try:
                age = time.time() - (d / "state.txt").stat().st_mtime
                rows.append(("ok" if age < 15 else "info", "file bridge", f"{d}: state.txt written {age:.0f} s ago"
                             + ("" if age < 15 else " (an idle bridge writes it rarely; a paused game not at all)")))
            except OSError:
                rows.append(("warn", "file bridge", f"{d} has no state.txt: InsideDev did not write there in this game copy"))
    return rows
