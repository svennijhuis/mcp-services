#!/usr/bin/env bash
# Build and test everything. Usage: scripts/build.sh [--no-test] [--configuration Release]
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/servers.sh"
cd "$REPO_ROOT"

configuration="Debug"
run_tests=1
while [ $# -gt 0 ]; do
  case "$1" in
    --no-test) run_tests=0 ;;
    -c|--configuration) configuration="$2"; shift ;;
    *) echo "Unknown option $1" >&2; exit 2 ;;
  esac
  shift
done

dotnet restore McpServices.slnx
dotnet build McpServices.slnx --no-restore --configuration "$configuration"
if [ "$run_tests" = 1 ]; then
  dotnet test McpServices.slnx --no-build --configuration "$configuration"
fi
