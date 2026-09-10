# mcp-index

A persistent, incremental index of one or more repositories so every new agent session starts with knowledge instead of `grep`. It stores file contents (hashed), extracted symbols (C# via the Roslyn syntax API, headings for markdown, generic fallbacks), full-text search, optional embeddings, and free-form notes that agents leave for their successors.

Project: `src/servers/McpServices.Index` · Tests: `tests/McpServices.Index.Tests`

## Running

```
mcp-index [transport options] [--root <dir>]... [--store sqlite:<path>|postgres:<conn>] [--auto-refresh inline|background|off]
mcp-index index|status|verify|rebuild <dir> [--store ...] [--force] [--quiet]      # CLI mode (git hooks, CI)
```

| Option | Meaning |
| --- | --- |
| `--root <dir>` (repeatable, positional) | Repositories tools may index. Default: any directory passed to a tool. |
| `--restrict` | Only allow repositories under the roots. |
| `--store <spec>` | `sqlite:<file>` (default `~/.mcp-services/mcp-index/index.db`) or `postgres:<connection-string>`; env `MCP_INDEX_STORE`. |
| `--auto-refresh <mode>` | `inline` (default): refresh small deltas before answering; `background`: answer now, refresh later; `off`. |
| `--inline-max-files <n>` | Largest delta still refreshed inline (default 200); bigger deltas go to the background. |
| `--max-file-kb <n>` | Skip files larger than this (default 512). |
| `--embeddings <spec>` | `ollama:<model>[@url]` or `openai:<model>[@url]`; env `MCP_INDEX_EMBEDDINGS`, key from `OPENAI_API_KEY`, url override `MCP_INDEX_EMBEDDINGS_URL`. |
| `--watch` | Watch the roots and refresh in the background on file changes. |

`MCP_SERVICES_HOME` moves the default data directory.

## Tools

| Tool | Purpose |
| --- | --- |
| `index_repository` | Incremental index: new/changed files processed, deleted files removed. Safe to call often. |
| `reindex` | Drop and rebuild one repository (notes and feedback are kept). |
| `index_status` | Counts, last run, freshness versus git HEAD and the working tree, list of changed files. |
| `verify_index` | Integrity check (content mismatches, orphans, FTS consistency); `repair: true` fixes what it finds. |
| `search_code` | Hybrid search: BM25 full text + exact symbol match + feedback boosts + embeddings (if configured), fused with reciprocal rank fusion. Returns a `queryId` for feedback. |
| `search_symbols` | Types, members, functions, headings by (partial) name. |
| `get_symbol` | Declaration(s) with source for an exact or fully qualified name. |
| `get_file_outline` | Symbols of one file in source order with line ranges. |
| `find_related_files` | Git co-change analysis plus shared symbols. |
| `remember` / `recall` / `forget` | Notes for future sessions, linked to files; `recall` flags `possiblyStale` when a linked file changed. |
| `mark_useful` | Boost hits or notes that helped; decays over time. |
| `list_repositories` / `forget_repository` | Manage what the store knows. |

Resource: `index://{repoId}/status`.

## Staying in sync when the MCP is not used

The index is only valuable if it reflects the checkout, and the checkout also changes outside agent sessions (manual edits, `git pull`, branch switches, CI). Defences, in layers:

1. **Content hashes, not timestamps.** Every file row stores a hash; refresh compares hashes, so touched-but-unchanged files are free and edited files are never missed.
2. **Freshness check on every read.** Read tools compare git HEAD and the working tree against the last indexed state. With `inline` they refresh first when the delta is small; with `background` they answer and refresh after; the response carries a `freshness` block (`stale`, changed files, indexed vs current HEAD, `refreshAction`) either way. `stale` is `null` when the check could not decide (huge checkout without git).
3. **Git hooks.** `scripts/install-git-hooks.sh [repo]` adds `post-checkout`, `post-merge` and `post-commit` hooks that run `mcp-index index <repo> --quiet`. Existing hooks are preserved (the call is appended). Disable temporarily with `MCP_INDEX_HOOKS=off`.
4. **`--watch`.** A file-system watcher for long-running servers (Docker, HTTP mode).
5. **CLI mode in CI.** `mcp-index index .` after checkout, `mcp-index verify .` to fail a pipeline on corruption.
6. **`verify_index` / `reindex`.** Recovery when something did go wrong (for example a store copied between machines).

Notes survive all of this: `reindex` and `forget_repository` treat notes differently (kept for reindex, removed only when forgetting the repository).

## Client configuration

```json
{
  "mcpServers": {
    "index": {
      "command": "mcp-index",
      "args": ["--root", "/home/sven/src/my-repo", "--auto-refresh", "inline"]
    }
  }
}
```

Docker: the `index` service uses the bundled PostgreSQL (`MCP_INDEX_STORE=postgres:...`), `--watch --auto-refresh background`, and optional `MCP_INDEX_EMBEDDINGS` on `http://127.0.0.1:5103/mcp`.

## Storage notes

- SQLite uses FTS5 for full text; PostgreSQL uses `tsvector` with GIN indexes and pgvector for embeddings (falls back to in-process cosine similarity when the extension cannot be created).
- The same `IKnowledgeStore` abstraction backs `mcp-learnings`; both can share a PostgreSQL database.
- One store can hold many repositories; each is keyed by a `repoId` hashed from the canonical root path (symlinks resolved), so two worktrees of the same repository get separate indexes and the same path always maps to the same id.
