# mcp-services

A monorepo of [Model Context Protocol](https://modelcontextprotocol.io) servers written in C# on .NET 10, built on the official [`ModelContextProtocol`](https://www.nuget.org/packages/ModelContextProtocol) SDK. Every server is a single console binary that speaks **stdio** (Cursor, Claude Desktop, VS Code) and, with `--http`, **Streamable HTTP** (Docker, remote bots).

| Server | Command | What it does | Docs |
|---|---|---|---|
| FileSystem | `mcp-filesystem` | Sandboxed file access: read/write/edit with diff preview, search, trees | [docs/servers/filesystem.md](docs/servers/filesystem.md) |
| Database | `mcp-database` | SQLite, PostgreSQL and SQL Server: schema discovery, guarded read queries, optional writes, exports | [docs/servers/database.md](docs/servers/database.md) |
| Roslyn | `mcp-roslyn` | C#/.NET analysis on real solutions: navigation, references, diagnostics, metrics, refactorings with preview, scripting, build/test | [docs/servers/roslyn.md](docs/servers/roslyn.md) |
| Index | `mcp-index` | Learning codebase index: incremental symbol + full-text (+ embeddings) search with feedback, notes, staleness detection, git hooks | [docs/servers/index.md](docs/servers/index.md) |
| Learnings | `mcp-learnings` | Self-learning loop: agents record what worked/failed, get recommendations, corroborated learnings become draft-PR proposals via Cursor | [docs/servers/learnings.md](docs/servers/learnings.md) |

Shared libraries: `McpServices.Hosting` (transports, CLI parsing, JSON/paging/error helpers, `server_info` tool) and `McpServices.Storage` (`IKnowledgeStore` over SQLite or PostgreSQL + pgvector, used by Index and Learnings).

## Quick start

Requirements: [.NET SDK 10.0](https://dotnet.microsoft.com/download) (`global.json` pins 10.0.4xx). `git` is optional but used by `mcp-index` for freshness tracking.

```bash
git clone https://github.com/svennijhuis/mcp-services && cd mcp-services
scripts/build.sh                 # restore, build, test
scripts/install-tools.sh         # installs mcp-filesystem, mcp-database, mcp-roslyn, mcp-index, mcp-learnings as .NET global tools
mcp-roslyn --help
```

Then add the servers to your client. For Cursor, `.cursor/mcp.json`:

```json
{
  "mcpServers": {
    "filesystem": { "command": "mcp-filesystem", "args": ["${workspaceFolder}"] },
    "roslyn":     { "command": "mcp-roslyn",     "args": ["--root", "${workspaceFolder}", "--restrict"] },
    "index":      { "command": "mcp-index",      "args": ["--root", "${workspaceFolder}", "--watch"] },
    "learnings":  { "command": "mcp-learnings",  "args": ["--repo", "owner/name"] },
    "database":   { "command": "mcp-database",   "args": ["--db", "app=sqlite:${workspaceFolder}/app.db", "--read-only"] }
  }
}
```

Ready-made configurations for Cursor, Claude Desktop, VS Code, the [agentPacks](https://github.com/svennijhuis/agentPacks) plugin schema and the Docker/HTTP stack are in [`examples/`](examples). All the ways to run the servers (from source, published binary, global tool, Docker) are described in [docs/LOCAL_USAGE.md](docs/LOCAL_USAGE.md).

## Docker (shared store for teams and remote agents)

```bash
cd docker && docker compose up -d --build
```

Starts PostgreSQL with pgvector plus all five servers over Streamable HTTP on `localhost:5100-5104` (`/mcp`). Index and Learnings use the shared PostgreSQL store, so every agent that connects learns from the same data; without Docker they fall back to SQLite under `~/.mcp-services`.

## Repository layout

```
src/shared/McpServices.Hosting      shared host: stdio/HTTP, CommandLine, ToolJson, Paging, TextDiff, ToolException
src/shared/McpServices.Storage      IKnowledgeStore: SqliteStore | PostgresStore, migrations, RRF, SecretRedactor
src/servers/McpServices.*           one project per server (ToolCommandName mcp-*)
tests/                              xunit; servers are tested as child processes over stdio with the SDK client
tests/fixtures/SampleSolution       two-project solution used by the Roslyn tests
examples/                           client configurations
docker/                             Dockerfiles + docker-compose.yml (pgvector)
scripts/                            build, publish, pack, install-tools, git hooks for mcp-index
docs/                               architecture, adding a server, local usage, per-server docs
```

## Design principles

- One binary, two transports; logs always go to stderr so stdio stays clean.
- Every tool validates input and throws `ToolException`, which the SDK turns into an `isError` result the agent can read; nothing else leaks.
- Destructive operations (`edit_file`, `write_query`, `rename_symbol`, `apply_code_fix`, ...) default to a dry run or require an explicit flag, and return unified diffs.
- Results are paginated (`pageToken`) and capped, so a tool call never floods the context window.
- Configuration lives in `args` and environment variables, never in the command; credentials (`CURSOR_API_KEY`, `OPENAI_API_KEY`, connection passwords) are read from the process environment only.
- Shared storage abstraction: SQLite for a single developer, PostgreSQL + pgvector for shared/remote use, same code path.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the internals and [docs/ADDING_A_SERVER.md](docs/ADDING_A_SERVER.md) to add a sixth server.

## Development

```bash
dotnet build McpServices.slnx
dotnet test McpServices.slnx
dotnet run --project src/servers/McpServices.Roslyn -- --root /path/to/solution   # run from source over stdio
dotnet run --project src/servers/McpServices.Index -- --http --port 5103 --root . # or over HTTP
```

PostgreSQL contract tests (storage and database server) are skipped unless `MCP_TEST_POSTGRES` holds a connection string. CI (`.github/workflows/ci.yml`) restores, builds, tests and packs the tools on every push and pull request.

## License

MIT, see [LICENSE](LICENSE). Open-source MCP servers that served as reference are credited in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
