#!/bin/sh
# Builds bin/InsideDev.dll (Roslyn csc from a .NET SDK) against the game's assemblies.
#   GAME = INSIDE install folder. Needs GAME/GameCode/{UnityEngine,PlayMaker,Assembly-CSharp,Assembly-CSharp-firstpass}.dll
#          (see README "Game code") and GAME/INSIDE_Data/Managed (mscorlib, System, System.Core, System.Xml).
set -e
: "${GAME:?set GAME to the INSIDE folder}"
G="$GAME/GameCode"; M="$GAME/INSIDE_Data/Managed"
CSC=${CSC:-$(ls /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll 2>/dev/null | head -1)}
for f in Assembly-CSharp.dll Assembly-CSharp-firstpass.dll UnityEngine.dll PlayMaker.dll; do
  [ -f "$G/$f" ] || { echo "missing $G/$f (see README: Game code)"; exit 1; }
done
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; mkdir -p "$ROOT/bin"
cd "$ROOT/src/InsideDev"
dotnet "$CSC" -nologo -noconfig -nostdlib -t:library -langversion:7.3 -optimize+ -unsafe -out:"$ROOT/bin/InsideDev.dll" \
  -nowarn:0618,0414,0649,0169,0219,0162,0168 \
  -r:"$M/mscorlib.dll" -r:"$M/System.dll" -r:"$M/System.Core.dll" -r:"$M/System.Xml.dll" \
  -r:"$G/UnityEngine.dll" -r:"$G/PlayMaker.dll" -r:"$G/Assembly-CSharp-firstpass.dll" -r:"$G/Assembly-CSharp.dll" \
  $(find . -name "*.cs" | sort)
