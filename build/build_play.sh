#!/bin/sh
# Builds bin/InsideDev.Play.dll (optional MCP play tools: character control and world tools). Needs bin/InsideDev.dll and
# bin/InsideDev.Mcp.dll first (build_mod.sh, build_mcp.sh).
set -e
: "${GAME:?set GAME to the INSIDE folder}"
G="$GAME/GameCode"; M="$GAME/INSIDE_Data/Managed"
CSC=${CSC:-$(ls /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll "/c/Program Files/dotnet/sdk/"*/Roslyn/bincore/csc.dll 2>/dev/null | tail -1)}
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT/src/play"
dotnet "$CSC" -nologo -noconfig -nostdlib -t:library -langversion:7.3 -optimize+ -unsafe -out:"$ROOT/bin/InsideDev.Play.dll" \
  -nowarn:0618,0414,0649,0169,0219,0162,0168,0067 \
  -r:"$M/mscorlib.dll" -r:"$M/System.dll" -r:"$M/System.Core.dll" -r:"$M/System.Xml.dll" \
  -r:"$G/UnityEngine.dll" -r:"$G/PlayMaker.dll" -r:"$G/Assembly-CSharp-firstpass.dll" -r:"$G/Assembly-CSharp.dll" \
  -r:"$ROOT/bin/InsideDev.dll" -r:"$ROOT/bin/InsideDev.Mcp.dll" $(ls *.cs | sort)
