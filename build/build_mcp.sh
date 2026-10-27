#!/bin/sh
# Builds bin/InsideDev.Mcp.dll (game extension; needs bin/InsideDev.dll first) and tools/mcp/InsideDev.McpStdio.exe.
#   NETFX = .NET Framework 4.x reference assemblies for the stdio launcher (mscorlib, System, System.Core); found
#           automatically in the usual Windows and Mono folders.
set -e
: "${GAME:?set GAME to the INSIDE folder}"
G="$GAME/GameCode"; M="$GAME/INSIDE_Data/Managed"
CSC=${CSC:-$(ls /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll "/c/Program Files/dotnet/sdk/"*/Roslyn/bincore/csc.dll 2>/dev/null | tail -1)}
if [ -z "$NETFX" ]; then
  for d in "/c/Program Files (x86)/Reference Assemblies/Microsoft/Framework/.NETFramework/v4.6.2" \
           "/c/Windows/Microsoft.NET/Framework64/v4.0.30319" /usr/lib/mono/4.6.2-api /usr/lib/mono/4.5; do
    [ -f "$d/mscorlib.dll" ] && { NETFX=$d; break; }
  done
fi
: "${NETFX:?set NETFX to a folder with the .NET Framework 4.x mscorlib.dll, System.dll and System.Core.dll}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; S="$ROOT/src/mcp"
cd "$S"
dotnet "$CSC" -nologo -noconfig -nostdlib -t:library -langversion:7.3 -optimize+ -unsafe -out:"$ROOT/bin/InsideDev.Mcp.dll" \
  -nowarn:0618,0414,0649,0169,0219,0162,0168,0067 \
  -r:"$M/mscorlib.dll" -r:"$M/System.dll" -r:"$M/System.Core.dll" -r:"$M/System.Xml.dll" \
  -r:"$G/UnityEngine.dll" -r:"$G/PlayMaker.dll" -r:"$G/Assembly-CSharp-firstpass.dll" -r:"$G/Assembly-CSharp.dll" \
  -r:"$ROOT/bin/InsideDev.dll" $(ls *.cs | grep -v '^McpStdio\.cs$' | sort)
# stdio launcher: plain .NET Framework 4 console app built from the Unity-free files only
mkdir -p "$ROOT/tools/mcp"
dotnet "$CSC" -nologo -noconfig -nostdlib -t:exe -langversion:7.3 -optimize+ -main:InsideDev.McpExt.StdioProxy \
  -out:"$ROOT/tools/mcp/InsideDev.McpStdio.exe" -nowarn:0618,0414,0649,0169,0219,0162,0168,0067 \
  -r:"$NETFX/mscorlib.dll" -r:"$NETFX/System.dll" -r:"$NETFX/System.Core.dll" \
  McpStdio.cs McpServer.cs $(ls Server*.cs Sse*.cs | sort) stdio/Json.cs
