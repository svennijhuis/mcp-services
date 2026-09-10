#!/usr/bin/env bash
# Publish one or all servers as self-contained single-file executables.
# Usage: scripts/publish.sh [server ...] [--rid linux-x64|osx-arm64|win-x64] [--framework-dependent] [--out artifacts]
# Result: <out>/<rid>/<server>/<server>[.exe] — copy the folder anywhere and point mcp.json at the executable.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/servers.sh"
cd "$REPO_ROOT"

rid=""
self_contained=true
out="artifacts"
selected=()
while [ $# -gt 0 ]; do
  case "$1" in
    --rid) rid="$2"; shift ;;
    --framework-dependent) self_contained=false ;;
    --out) out="$2"; shift ;;
    -*) echo "Unknown option $1" >&2; exit 2 ;;
    *) selected+=("$1") ;;
  esac
  shift
done

if [ -z "$rid" ]; then
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) rid="linux-x64" ;;
    Linux-aarch64) rid="linux-arm64" ;;
    Darwin-arm64) rid="osx-arm64" ;;
    Darwin-x86_64) rid="osx-x64" ;;
    MINGW*|MSYS*|CYGWIN*) rid="win-x64" ;;
    *) echo "Cannot detect RID; pass --rid" >&2; exit 2 ;;
  esac
fi

if [ ${#selected[@]} -eq 0 ]; then
  selected=("${SERVERS[@]%%=*}")
fi

for server in "${selected[@]}"; do
  project="$(server_project "$server")"
  echo "==> publishing $server ($rid, self-contained=$self_contained)"
  # mcp-roslyn loads the MSBuild of the installed SDK at runtime, so it is always framework-dependent.
  sc="$self_contained"
  if [ "$server" = "mcp-roslyn" ]; then sc=false; fi
  dotnet publish "$project" \
    --configuration Release \
    --runtime "$rid" \
    --self-contained "$sc" \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=none \
    --output "$out/$rid/$server"
done

echo
echo "Published to $out/$rid/. Example mcp.json entry:"
echo "  \"mcp-filesystem\": { \"command\": \"$(cd "$out" && pwd)/$rid/mcp-filesystem/mcp-filesystem\", \"args\": [\"/path/to/project\"] }"
