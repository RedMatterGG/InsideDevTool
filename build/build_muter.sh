#!/bin/sh
# Builds tools/muter/InsideMuter.exe (optional, .NET Framework 4 console program, x64). The source is C# 5.
#   NETFX = .NET Framework 4.x reference assemblies (mscorlib, System, System.Core); found automatically on Windows.
set -e
CSC=${CSC:-$(ls /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll "/c/Program Files/dotnet/sdk/"*/Roslyn/bincore/csc.dll 2>/dev/null | tail -1)}
if [ -z "$NETFX" ]; then
  for d in "/c/Program Files (x86)/Reference Assemblies/Microsoft/Framework/.NETFramework/v4.6.2" \
           "/c/Windows/Microsoft.NET/Framework64/v4.0.30319" /usr/lib/mono/4.6.2-api /usr/lib/mono/4.5; do
    [ -f "$d/mscorlib.dll" ] && { NETFX=$d; break; }
  done
fi
: "${NETFX:?set NETFX to a folder with the .NET Framework 4.x mscorlib.dll, System.dll and System.Core.dll}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; mkdir -p "$ROOT/tools/muter"
dotnet "$CSC" -nologo -noconfig -nostdlib -t:exe -platform:x64 -langversion:5 -optimize+ -out:"$ROOT/tools/muter/InsideMuter.exe" \
  -r:"$NETFX/mscorlib.dll" -r:"$NETFX/System.dll" -r:"$NETFX/System.Core.dll" "$ROOT/src/muter/InsideMuter.cs"
