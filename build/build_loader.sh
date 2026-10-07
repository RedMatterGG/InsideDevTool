#!/bin/sh
# Builds bin/version.dll, the proxy loader (mingw-w64 cross compiler).
set -e
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT/src/loader"
x86_64-w64-mingw32-gcc -O2 -shared -Wall -Wno-cast-function-type -o "$ROOT/bin/version.dll" version_proxy.c wwise_tracer.c version.def -static-libgcc -s
