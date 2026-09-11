# mcp-filesystem

Sandboxed file system access. Every path is resolved (symlinks included) and must end up inside one of the allowed directories; anything else is rejected before touching the disk.

Project: `src/servers/McpServices.FileSystem` · Tests: `tests/McpServices.FileSystem.Tests`

## Running

```
mcp-filesystem [transport options] <allowed-dir> [<allowed-dir> ...]
```

| Setting | How | Notes |
| --- | --- | --- |
| Allowed directories | positional args or `MCP_FS_ALLOWED_DIRS` | Path-separator delimited (`:` on Linux/macOS, `;` on Windows). Relative paths passed to tools resolve against the first allowed directory. |
| Transport | `--http --port 5100 --host 127.0.0.1` | Default is stdio. `MCP_PORT`/`MCP_HOST` also work. Non-loopback `--host` requires `--auth-token` / `MCP_AUTH_TOKEN`. |

Without any allowed directory the server starts but every tool call fails with a clear error, so a misconfiguration never turns into unrestricted access.

## Tools

| Tool | Purpose |
| --- | --- |
| `read_text_file` | Read a UTF-8 file, optionally only the first `head` or last `tail` lines. |
| `read_media_file` | Return an image/audio file base64-encoded with its MIME type. |
| `read_multiple_files` | Read several files; per-file failures are reported instead of aborting. |
| `write_file` | Create or overwrite a file (parents created, atomic write via temp file + rename). |
| `edit_file` | Line-based edits (`oldText` → `newText`), exact match first, whitespace-insensitive fallback. Returns a unified diff; `dryRun: true` previews without writing. |
| `create_directory` | `mkdir -p`. |
| `list_directory` | Entries prefixed with `[FILE]`/`[DIR]`. |
| `list_directory_with_sizes` | Sizes and modification times, sorted by name or size, with totals. |
| `directory_tree` | Recursive JSON tree with `excludePatterns` (glob). |
| `move_file` | Move/rename; refuses to overwrite. |
| `search_files` | Glob search (`*.cs`, `**/*.cs`) or plain word substring match, with exclude patterns. |
| `get_file_info` | Size, timestamps, type, permissions, symlink target. |
| `list_allowed_directories` | The sandbox boundaries. |

All write tools are annotated as non-read-only; `write_file` and `move_file` are flagged destructive so clients can ask for confirmation.

## Client configuration

```json
{
  "mcpServers": {
    "filesystem": {
      "command": "mcp-filesystem",
      "args": ["/home/sven/src/my-repo", "/home/sven/notes"]
    }
  }
}
```

Docker (`docker/docker-compose.yml`): the `filesystem` service mounts `${WORKSPACE:-..}` at `/workspace` and listens on `http://127.0.0.1:5100/mcp`.

## Behaviour worth knowing

- `read_text_file` has no size cap; use `head`/`tail` for large logs so the response stays small.
- `search_files` is paged (`pageSize` default 50, max 500).
- `edit_file` preserves the file's existing line endings and requires every `oldText` to match exactly once; ambiguous matches fail rather than guessing.
- Hidden directories such as `.git`, `node_modules`, `bin` and `obj` are still listed; use `excludePatterns` to hide them.
