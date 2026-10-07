#!/bin/sh
# Builds bin/InsideDev.Mcp.dll (game extension; needs bin/InsideDev.dll first) and tools/mcp/InsideDev.McpStdio.exe.
set -e
: "${GAME:?set GAME to the INSIDE folder}"
G="$GAME/GameCode"; M="$GAME/INSIDE_Data/Managed"
CSC=${CSC:-$(ls /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll 2>/dev/null | head -1)}
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; S="$ROOT/src/mcp"
dotnet "$CSC" -nologo -noconfig -nostdlib -t:library -langversion:7.3 -optimize+ -nowarn:0618 -out:"$ROOT/bin/InsideDev.Mcp.dll" \
  -r:"$M/mscorlib.dll" -r:"$M/System.dll" -r:"$M/System.Core.dll" -r:"$G/UnityEngine.dll" -r:"$ROOT/bin/InsideDev.dll" \
  "$S/McpServer.cs" "$S/Extension.cs" "$S/Png.cs" "$S/Zlib.cs"
# stdio launcher: plain .NET Framework 4 console app (Mono's mcs, or csc.exe from Windows' .NET Framework folder)
mkdir -p "$ROOT/tools/mcp"
mcs -sdk:4.5 -optimize+ -out:"$ROOT/tools/mcp/InsideDev.McpStdio.exe" -main:InsideDev.McpExt.StdioProxy \
  "$S/McpStdio.cs" "$S/McpServer.cs" "$ROOT/src/InsideDev/Runtime/Json.cs"
