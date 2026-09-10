#!/usr/bin/env bash
# Install (or update) the servers as .NET global tools so that `mcp-filesystem`, `mcp-database`,
# `mcp-roslyn`, `mcp-index` and `mcp-learnings` are on PATH — which is exactly what the agentPacks
# mcp.json schema needs (bare command tokens, no paths).
# Usage: scripts/install-tools.sh [server ...] [--source nupkg]
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/servers.sh"
cd "$REPO_ROOT"

source_dir="nupkg"
selected=()
while [ $# -gt 0 ]; do
  case "$1" in
    --source) source_dir="$2"; shift ;;
    -*) echo "Unknown option $1" >&2; exit 2 ;;
    *) selected+=("$1") ;;
  esac
  shift
done

if [ ! -d "$source_dir" ] || [ -z "$(ls -A "$source_dir" 2>/dev/null)" ]; then
  "$REPO_ROOT/scripts/pack-tools.sh" "$source_dir"
fi

if [ ${#selected[@]} -eq 0 ]; then
  selected=("${SERVERS[@]%%=*}")
fi

version="$(grep -oPm1 '(?<=<Version>)[^<]+' Directory.Build.props)"
for server in "${selected[@]}"; do
  project="$(server_project "$server")"
  package="$(grep -oPm1 '(?<=<PackageId>)[^<]+' "$project")"
  echo "==> $server ($package $version)"
  if dotnet tool list --global | grep -qi "^$package "; then
    dotnet tool update --global "$package" --add-source "$source_dir" --version "$version"
  else
    dotnet tool install --global "$package" --add-source "$source_dir" --version "$version"
  fi
done

tools_dir="$HOME/.dotnet/tools"
case ":$PATH:" in
  *":$tools_dir:"*) ;;
  *) echo; echo "Add $tools_dir to your PATH so MCP clients can find the commands (e.g. export PATH=\"\$PATH:$tools_dir\")." ;;
esac
echo
echo "Installed. Try: mcp-filesystem --help"
