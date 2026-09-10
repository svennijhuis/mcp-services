#!/usr/bin/env bash
# Shared list of server projects; sourced by the other scripts.
# Each entry: <tool command>=<project path>
SERVERS=(
  "mcp-filesystem=src/servers/McpServices.FileSystem/McpServices.FileSystem.csproj"
  "mcp-database=src/servers/McpServices.Database/McpServices.Database.csproj"
  "mcp-roslyn=src/servers/McpServices.Roslyn/McpServices.Roslyn.csproj"
  "mcp-index=src/servers/McpServices.Index/McpServices.Index.csproj"
  "mcp-learnings=src/servers/McpServices.Learnings/McpServices.Learnings.csproj"
)

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

server_project() {
  local wanted="$1"
  for entry in "${SERVERS[@]}"; do
    if [ "${entry%%=*}" = "$wanted" ]; then
      echo "${entry#*=}"
      return 0
    fi
  done
  echo "Unknown server '$wanted'. Known: ${SERVERS[*]%%=*}" >&2
  return 1
}
