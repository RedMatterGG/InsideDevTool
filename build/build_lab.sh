#!/bin/sh
# Builds the optional lab modules bin/InsideDev.ReplayLab.dll (lab tests L1-L12) and bin/InsideDev.Det.dll (input replay
# determinism test). Needs bin/InsideDev.dll first (build_mod.sh).
set -e
: "${GAME:?set GAME to the INSIDE folder}"
G="$GAME/GameCode"; M="$GAME/INSIDE_Data/Managed"
CSC=${CSC:-$(ls /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll "/c/Program Files/dotnet/sdk/"*/Roslyn/bincore/csc.dll 2>/dev/null | tail -1)}
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
for m in replaylab:InsideDev.ReplayLab det:InsideDev.Det; do
  cd "$ROOT/src/${m%%:*}"
  dotnet "$CSC" -nologo -noconfig -nostdlib -t:library -langversion:7.3 -optimize+ -unsafe -out:"$ROOT/bin/${m#*:}.dll" \
    -nowarn:0618,0414,0649,0169,0219,0162,0168 \
    -r:"$M/mscorlib.dll" -r:"$M/System.dll" -r:"$M/System.Core.dll" -r:"$M/System.Xml.dll" \
    -r:"$G/UnityEngine.dll" -r:"$G/PlayMaker.dll" -r:"$G/Assembly-CSharp-firstpass.dll" -r:"$G/Assembly-CSharp.dll" \
    -r:"$ROOT/bin/InsideDev.dll" $(ls *.cs | sort)
done
