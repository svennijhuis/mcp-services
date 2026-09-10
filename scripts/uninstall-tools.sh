#!/usr/bin/env bash
# Remove the global tools installed by install-tools.sh.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/servers.sh"
for entry in "${SERVERS[@]}"; do
  package="$(grep -oPm1 '(?<=<PackageId>)[^<]+' "$REPO_ROOT/${entry#*=}")"
  dotnet tool uninstall --global "$package" 2>/dev/null && echo "removed $package" || true
done
