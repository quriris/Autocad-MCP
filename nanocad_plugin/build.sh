#!/usr/bin/env bash
# Build McpSpds.dll without NuGet, straight with the Roslyn compiler.
#
#   NETCORE_REF=<folder with .NET 6 runtime DLLs>   (e.g. a copy of
#       C:\Program Files\dotnet\shared\Microsoft.NETCore.App\6.0.x)
#   NANOCAD_BIN=<folder with mapimgd.dll and mapibasetypes.dll>  (e.g. a copy of
#       C:\Program Files\Nanosoft\nanoCAD x64 24.1\NanoSPDS\bin)
#   CSC=<path to csc.dll>   (default: the .NET 8 SDK's Roslyn)
#
# The nanoCAD and .NET DLLs are referenced, never copied into the output or
# the repository. On Windows, McpSpds.csproj does the same with `dotnet build`.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
: "${NETCORE_REF:?set NETCORE_REF}"
: "${NANOCAD_BIN:?set NANOCAD_BIN}"
CSC="${CSC:-$(ls -d /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll 2>/dev/null | tail -1)}"
refs=()
for f in "$NETCORE_REF"/*.dll; do refs+=("-r:$f"); done
mkdir -p "$here/bin"
dotnet "$CSC" -nologo -noconfig -nostdlib -target:library -langversion:10 \
  -nullable:enable -platform:x64 -optimize -deterministic \
  "${refs[@]}" "-r:$NANOCAD_BIN/mapimgd.dll" "-r:$NANOCAD_BIN/mapibasetypes.dll" \
  -out:"$here/bin/McpSpds.dll" "$here/McpSpds/McpSpds.cs"
echo "built $here/bin/McpSpds.dll"
