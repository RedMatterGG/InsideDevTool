"""ffmpeg / ffprobe children of the mcam tools, in one place: every child is started from an argument list (no shell), its
stderr is drained on a thread, it is always waited for, it is killed and waited for on an error or Ctrl+C, its exit code is
checked and its last error lines are part of the exception (FfError). Text is decoded as UTF-8 with replacement.
run() is for short children (ffprobe, encoder lists), Child for a child that is fed frames or read from, frames() yields raw
frames of a decode and fails when the stream ends with an error or inside a frame, probe() is ffprobe as JSON.
Also the exit code contract of the tools (0 ok, 1 the result is bad, 2 input or usage error, 3 refused) and the UTF-8 console
setup. Executables: environment FFMPEG / FFPROBE, else PATH.
Usage: python -I tools/mcam_ff.py [--json]   prints what was found (exit 0, or 2 when ffmpeg or ffprobe is missing)"""
from __future__ import annotations

import argparse
import collections
import json
import os
import re
import shutil
import subprocess
import sys
import threading
from pathlib import Path

EXIT_OK, EXIT_BAD, EXIT_INPUT, EXIT_REFUSED = 0, 1, 2, 3
TAIL_BYTES = 1000
TAIL_LINES = 6
WAIT_KILLED = 10.0
PROBE_ENTRIES = ('stream=index,codec_type,codec_name,codec_tag_string,profile,pix_fmt,width,height,nb_frames,{count}'
                 'color_space,color_transfer,color_primaries,color_range,r_frame_rate,sample_rate,channels,duration'
                 ':stream_tags=DURATION:format=duration,size')

_EXE: dict[str, str] = {}
_LISTS: dict[str, set] = {}


class FfError(Exception):
    """A child could not start, ended with an error or gave a broken stream. `code` is its exit code (None when it never
    ran or was killed), `tail` its last error lines."""

    def __init__(self, message: str, code: int | None = None, tail: str = '', cmd: list | None = None):
        super().__init__(message)
        self.code = code
        self.tail = tail
        self.cmd = list(cmd or [])


class Done:
    __slots__ = ('code', 'out', 'err')

    def __init__(self, code: int, out: str, err: str):
        self.code = code
        self.out = out
        self.err = err


def utf8_stdio() -> None:
    for s in (sys.stdout, sys.stderr):
        try:
            s.reconfigure(encoding='utf-8', errors='replace')
        except (AttributeError, ValueError, OSError):
            pass


def exe(name: str) -> str:
    hit = _EXE.get(name)
    if hit is None:
        hit = _EXE[name] = os.environ.get(name.upper()) or shutil.which(name) or name
    return hit


def ffmpeg() -> str:
    return exe('ffmpeg')


def ffprobe() -> str:
    return exe('ffprobe')


def tail_text(data: bytes | str, lines: int = TAIL_LINES, limit: int = TAIL_BYTES) -> str:
    """The error lines worth showing: ffmpeg names the cause first and the consequences last, so of a longer text the
    first and the last lines are kept (pointer values removed)."""
    text = data.decode('utf-8', 'replace') if isinstance(data, (bytes, bytearray)) else str(data)
    rows = [re.sub(r' @ [0-9a-fA-F]{6,}', '', r.strip())[:240] for r in text.replace('\r', '\n').split('\n') if r.strip()]
    if len(rows) > lines:
        head = lines // 2
        rows = rows[:head] + ['...'] + rows[-(lines - head):]
    return ' | '.join(rows)[:limit]


def child_name(cmd: list) -> str:
    return Path(str(cmd[0])).stem if cmd else 'child'


def start_error(name: str, cmd: list, e: OSError) -> FfError:
    return FfError(f'{name}: cannot start "{cmd[0]}" ({e.strerror or e}); put ffmpeg and ffprobe on PATH or set the FFMPEG / '
                   f'FFPROBE environment variables', None, '', cmd)


def exit_error(name: str, code: int | None, tail: str, cmd: list, what: str = '') -> FfError:
    state = 'was killed' if code is None else f'exit {code - (1 << 32) if code >= 1 << 31 else code}'
    return FfError(f'{name} {what + ", " if what else ""}{state}: {tail or "no error text"}', code, tail, cmd)


def run(cmd: list, timeout: float | None = 120.0, check: bool = True, name: str = '') -> Done:
    cmd = [str(c) for c in cmd]
    name = name or child_name(cmd)
    try:
        proc = subprocess.Popen(cmd, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    except OSError as e:
        raise start_error(name, cmd, e) from None
    try:
        out, err = proc.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        proc.kill()
        out, err = proc.communicate()
        raise FfError(f'{name}: no result after {timeout:g} s, killed: {tail_text(err) or "no error text"}', None, tail_text(err),
                      cmd) from None
    except BaseException:
        proc.kill()
        proc.communicate()
        raise
    res = Done(proc.returncode, out.decode('utf-8', 'replace'), err.decode('utf-8', 'replace'))
    if check and res.code != 0:
        raise exit_error(name, res.code, tail_text(res.err), cmd)
    return res


class Child:
    """A long-lived child. Use as a context manager: a clean exit of the block waits and checks the exit code (finish), an
    exception or Ctrl+C inside it kills the child and waits for it (kill)."""

    def __init__(self, cmd: list, stdin: bool = False, stdout: bool = False, name: str = ''):
        self.cmd = [str(c) for c in cmd]
        self.name = name or child_name(self.cmd)
        self.done = False
        self.code: int | None = None
        self._err: collections.deque = collections.deque(maxlen=32)
        try:
            self.proc = subprocess.Popen(self.cmd, stdin=subprocess.PIPE if stdin else subprocess.DEVNULL,
                                         stdout=subprocess.PIPE if stdout else subprocess.DEVNULL, stderr=subprocess.PIPE)
        except OSError as e:
            self.done = True
            raise start_error(self.name, self.cmd, e) from None
        self._drain = threading.Thread(target=self._read_err, daemon=True)
        self._drain.start()

    def _read_err(self) -> None:
        try:
            while True:
                b = self.proc.stderr.read(4096)
                if not b:
                    return
                self._err.append(b)
        except (OSError, ValueError):
            return

    @property
    def tail(self) -> str:
        return tail_text(b''.join(list(self._err)))

    def write(self, data) -> None:
        try:
            self.proc.stdin.write(data)
        except (OSError, ValueError):
            raise self._stopped('stopped while it was fed') from None

    def read(self, size: int) -> bytes:
        try:
            return self.proc.stdout.read(size)
        except (OSError, ValueError):
            raise self._stopped('stopped while it was read') from None

    def _close_pipes(self) -> None:
        for p in (self.proc.stdin, self.proc.stdout):
            if p is not None:
                try:
                    p.close()
                except (OSError, ValueError):
                    pass

    def _reap(self, timeout: float | None) -> int | None:
        try:
            self.code = self.proc.wait(timeout)
        except subprocess.TimeoutExpired:
            try:
                self.proc.kill()
            except OSError:
                pass
            try:
                self.proc.wait(WAIT_KILLED)
            except subprocess.TimeoutExpired:
                pass
            self.code = None
        self._drain.join(5)
        try:
            self.proc.stderr.close()
        except (OSError, ValueError):
            pass
        self.done = True
        return self.code

    def _stopped(self, what: str) -> FfError:
        self._close_pipes()
        code = self._reap(WAIT_KILLED)
        return exit_error(self.name, code, self.tail, self.cmd, what)

    def finish(self, timeout: float | None = None) -> None:
        if self.done:
            return
        broken = False
        if self.proc.stdin is not None:
            try:
                self.proc.stdin.close()
            except (OSError, ValueError):
                broken = True
        code = self._reap(timeout)
        self._close_pipes()
        if code != 0:
            raise exit_error(self.name, code, self.tail, self.cmd, 'stopped while it was fed' if broken else '')

    def kill(self) -> None:
        if self.done:
            return
        if self.proc.poll() is None:
            try:
                self.proc.kill()
            except OSError:
                pass
        self._close_pipes()
        self._reap(WAIT_KILLED)

    def __enter__(self) -> 'Child':
        return self

    def __exit__(self, kind, value, trace) -> bool:
        if kind is None:
            self.finish()
        else:
            self.kill()
        return False


def frames(cmd: list, frame_bytes: int, name: str = ''):
    """Raw frames of `frame_bytes` each from the child's stdout. The stream ending by itself with a non-zero exit code or
    inside a frame raises FfError; a caller that stops early just ends the child."""
    if frame_bytes <= 0:
        raise FfError(f'{name or child_name(cmd)}: frame size {frame_bytes} (the video size is unknown)', None, '', cmd)
    ch = Child(cmd, stdout=True, name=name)
    try:
        count = 0
        while True:
            buf = ch.read(frame_bytes)
            if len(buf) < frame_bytes:
                break
            count += 1
            yield buf
        ch.finish()
        if buf:
            raise FfError(f'{ch.name}: the stream ended inside frame {count} ({len(buf)} of {frame_bytes} bytes)', 0, ch.tail,
                          ch.cmd)
    finally:
        ch.kill()


def probe(path, count: bool = False, entries: str | None = None, timeout: float | None = 600.0) -> dict:
    cmd = [ffprobe(), '-v', 'error', *(['-count_packets'] if count else []), '-show_entries',
           entries or PROBE_ENTRIES.format(count='nb_read_packets,' if count else ''), '-of', 'json', str(path)]
    res = run(cmd, timeout=timeout, name='ffprobe')
    try:
        doc = json.loads(res.out or '{}')
    except ValueError as e:
        raise FfError(f'ffprobe: unreadable answer for {Path(str(path)).name} ({e})', res.code, tail_text(res.err), cmd) from None
    return doc if isinstance(doc, dict) else {}


def video_size(path) -> tuple[int, int]:
    doc = probe(path, entries='stream=codec_type,width,height', timeout=120.0)
    for s in doc.get('streams') or []:
        if s.get('codec_type') == 'video' and int(s.get('width') or 0) > 0 and int(s.get('height') or 0) > 0:
            return int(s['width']), int(s['height'])
    raise FfError(f'ffprobe: no video stream with a size in {Path(str(path)).name}', 0, '', [])


def version(which: str = 'ffmpeg') -> tuple[str, tuple | None]:
    res = run([exe(which), '-hide_banner', '-version'], timeout=30.0, name=which)
    first = (res.out.splitlines() or [''])[0]
    m = re.search(r'version\s+(\S+)', first)
    text = m.group(1) if m else first.strip()
    n = re.match(r'n?(\d+)\.(\d+)(?:\.(\d+))?', text)
    return text, (tuple(int(g or 0) for g in n.groups()) if n else None)


def name_list(kind: str) -> set:
    hit = _LISTS.get(kind)
    if hit is None:
        res = run([ffmpeg(), '-hide_banner', '-' + kind], timeout=60.0, name='ffmpeg')
        hit = set()
        for line in res.out.splitlines():
            parts = line.split()
            if len(parts) >= 2 and not line.startswith(' ---') and '=' not in parts[0]:
                hit.add(parts[1])
        _LISTS[kind] = hit
    return hit


def encoders() -> set:
    return name_list('encoders')


def filters() -> set:
    return name_list('filters')


class JsonMode:
    """--json of a tool: while it is on, everything the tool prints goes to stderr; finish() writes the one JSON object
    to stdout."""

    def __init__(self, on: bool):
        self.on = bool(on)
        self.stdout = sys.stdout
        if self.on:
            sys.stdout = sys.stderr

    def finish(self, doc: dict) -> None:
        if self.on:
            sys.stdout = self.stdout
            print(json.dumps(doc, default=str), flush=True)


def main(argv: list | None = None) -> int:
    utf8_stdio()
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--json', action='store_true', help='one JSON object on stdout')
    a = ap.parse_args(argv)
    doc: dict = {'ok': True, 'exit': EXIT_OK}
    for which in ('ffmpeg', 'ffprobe'):
        try:
            text, num = version(which)
            doc[which] = {'path': exe(which), 'version': text, 'number': list(num) if num else None}
        except FfError as e:
            doc[which] = {'path': exe(which), 'error': str(e)}
            doc['ok'], doc['exit'] = False, EXIT_INPUT
    if a.json:
        print(json.dumps(doc))
    else:
        for which in ('ffmpeg', 'ffprobe'):
            d = doc[which]
            print(f'{which:<8} {d["path"]}  ' + (f'version {d["version"]}' if 'version' in d else f'error: {d["error"]}'))
    return doc['exit']


if __name__ == '__main__':
    sys.exit(main())
