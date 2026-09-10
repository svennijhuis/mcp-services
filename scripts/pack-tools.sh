#!/usr/bin/env bash
# Pack every server as a .NET global tool into ./nupkg (used by install-tools.sh).
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/servers.sh"
cd "$REPO_ROOT"

out="${1:-nupkg}"
rm -rf "$out"
for entry in "${SERVERS[@]}"; do
  dotnet pack "${entry#*=}" --configuration Release --output "$out"
done
ls -1 "$out"
