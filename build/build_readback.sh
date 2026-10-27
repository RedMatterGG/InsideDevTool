#!/bin/sh
# Builds bin/InsideReadback.dll, the optional native frame readback plugin for MultiCam (MinGW-w64 gcc).
set -e
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CC=${CC:-x86_64-w64-mingw32-gcc}
"$CC" -O2 -Wall -Wextra -shared -static-libgcc -o "$ROOT/bin/InsideReadback.dll" "$ROOT/src/readback/inside_readback.c"
