#!/usr/bin/env bash
# Installs the mcp-index git hooks into a repository (default: the current one).
# Existing hooks are kept: the mcp-index call is appended to them.
set -euo pipefail

target="${1:-$(git rev-parse --show-toplevel)}"
hooks_dir="$(git -C "$target" rev-parse --git-path hooks)"
source_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/git-hooks" && pwd)"

mkdir -p "$hooks_dir"
cp "$source_dir/mcp-index-refresh" "$hooks_dir/mcp-index-refresh"
chmod +x "$hooks_dir/mcp-index-refresh"

for hook in post-checkout post-merge post-commit; do
  if [ -f "$hooks_dir/$hook" ] && ! grep -q "mcp-index-refresh" "$hooks_dir/$hook"; then
    printf '\n# mcp-index\n"$(dirname "$0")/mcp-index-refresh" "$@" || true\n' >> "$hooks_dir/$hook"
  elif [ ! -f "$hooks_dir/$hook" ]; then
    cp "$source_dir/$hook" "$hooks_dir/$hook"
  fi
  chmod +x "$hooks_dir/$hook"
done

echo "mcp-index hooks installed in $hooks_dir (disable with MCP_INDEX_HOOKS=off)"
