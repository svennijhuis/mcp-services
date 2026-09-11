# Local usage

Every server is one console binary. Which way you start it only changes the `command`/`args` in your client configuration; the tools are identical.

| Option | Start-up | Best for |
|---|---|---|
| 1. `dotnet run` from source | slow (build on every start) | trying a change |
| 2. Published binary (`scripts/publish.sh`) | fast | one machine, no PATH changes, Claude Desktop |
| 3. .NET global tool (`scripts/install-tools.sh`) | fast | Cursor / VS Code / agentPacks: bare `mcp-*` commands on PATH |
| 4. Docker + Streamable HTTP | n/a | teams, remote bots, shared PostgreSQL store |

## 1. Run from source

```json
{ "command": "dotnet", "args": ["run", "--project", "/abs/mcp-services/src/servers/McpServices.FileSystem", "--", "/abs/repo"] }
```

Useful for development only: `dotnet run` compiles first and prints build output to stdout before the server starts, which some clients treat as a protocol error. Prefer option 2 or 3 for daily use.

## 2. Published binaries

```bash
scripts/publish.sh                    # all servers, current RID, self-contained single-file
scripts/publish.sh mcp-roslyn mcp-index --rid osx-arm64
```

Output: `artifacts/<rid>/<server>/<server>` (plus `.exe` on Windows). Self-contained binaries do not need a .NET runtime on the target machine. `mcp-roslyn` is always framework-dependent because it loads MSBuild from the installed SDK; run it on a machine with the .NET SDK.

```json
{ "command": "/abs/mcp-services/artifacts/linux-x64/mcp-filesystem/mcp-filesystem", "args": ["/abs/repo"] }
```

Framework-dependent variant (`--framework-dependent`): `"command": "dotnet", "args": ["/abs/artifacts/linux-x64/mcp-filesystem/McpServices.FileSystem.dll", "/abs/repo"]`.

## 3. .NET global tools (recommended)

```bash
scripts/install-tools.sh              # packs to ./nupkg and installs/updates all five tools
scripts/install-tools.sh mcp-roslyn   # one tool
scripts/uninstall-tools.sh
pwsh scripts/install-tools.ps1        # Windows
```

The tools land in `~/.dotnet/tools` (`%USERPROFILE%\.dotnet\tools` on Windows). Make sure that directory is on `PATH`; the installer prints a hint if it is not. Then any client can use bare commands:

```json
{ "command": "mcp-roslyn", "args": ["--root", "${workspaceFolder}", "--restrict"] }
```

Notes:

- If `dotnet` is installed in a non-standard location (e.g. `~/.dotnet` via `dotnet-install.sh`), the tool shims need `DOTNET_ROOT` to find the runtime. Set it globally, or per server via `"env": { "DOTNET_ROOT": "/home/you/.dotnet" }`.
- GUI clients (Claude Desktop, some Cursor installs) do not inherit your shell `PATH`. Use the full path to the shim, e.g. `~/.dotnet/tools/mcp-roslyn`, or a published binary.
- Upgrading: re-run `scripts/install-tools.sh`; it detects installed tools and runs `dotnet tool update`.

## 4. Docker and Streamable HTTP

```bash
cd docker
cp .env.example .env                 # set MCP_AUTH_TOKEN (required)
WORKSPACE=/abs/repo docker compose up -d --build
```

| Service | Port | Store |
|---|---|---|
| filesystem | 5100 | — (bind mount `/workspace`) |
| roslyn | 5101 | — (SDK image, `/workspace`, NuGet cache volume) |
| database | 5102 | exposes the shared PostgreSQL itself as alias `mcp` (read-only) |
| index | 5103 | PostgreSQL + pgvector |
| learnings | 5104 | PostgreSQL + pgvector |
| postgres | 5432 (loopback) | named volume `postgres-data` |

Clients connect with `{ "url": "http://localhost:5103/mcp", "headers": { "Authorization": "Bearer …" } }` (see `examples/docker.mcp.json`). Ports are bound to `127.0.0.1`. Copy `docker/.env.example` to `docker/.env` and set `MCP_AUTH_TOKEN` — containers bind `0.0.0.0` inside the network, so the host refuses to start without a bearer token. A reverse proxy or tunnel with TLS is still required before anything beyond your machine can reach `/mcp`; a tunnel to loopback is public, so the token is the access control.

Environment for compose (shell or a `.env` file next to `docker-compose.yml`, never committed): `MCP_AUTH_TOKEN` (required), `POSTGRES_PASSWORD`, `CURSOR_API_KEY`, `OPENAI_API_KEY`, `MCP_INDEX_EMBEDDINGS`, `MCP_LEARNINGS_REPO`, `MCP_LEARNINGS_TARGET_REPOS`, `MCP_LEARNINGS_DISPATCH`, `MCP_PUBLIC_URL`, `MCP_ALLOWED_HOSTS`. See [docker/.env.example](../docker/.env.example). Grok Bot setup is in [examples/grok-bot.md](../examples/grok-bot.md).

Single container over stdio (no compose): `docker run -i --rm -v /abs/repo:/workspace mcp-index:local --root /workspace`.

Any server can also run over HTTP without Docker: `mcp-index --http --port 5103 --root .` (binds `127.0.0.1`; `--host 0.0.0.0` to expose, which **requires** `--auth-token` / `MCP_AUTH_TOKEN`). Loopback without a token is for local clients only. Any public URL — including a Cloudflare/ngrok tunnel to `127.0.0.1` — must use a token; binding loopback is not a security boundary once a tunnel exists.

`GET /health` and `GET /healthz` stay unauthenticated for probes. The MCP endpoint is only `/mcp`.

## Shared options

Every server understands:

```
--http                 Streamable HTTP instead of stdio
--port <n>             HTTP port (default 5100; env MCP_PORT)
--host <name>          HTTP bind address (default 127.0.0.1)
--auth-token <token>   Require Authorization: Bearer on /mcp (env MCP_AUTH_TOKEN).
                       Mandatory when --host is not loopback; mandatory for any public URL
--public-url <url>     Advertised https:// origin (tunnel/proxy); env MCP_PUBLIC_URL
--allowed-host <name>  Extra Host values (repeatable; env MCP_ALLOWED_HOSTS). Pass * for tunnels
--cors-origin <origin> Opt-in browser CORS (repeatable; env MCP_CORS_ORIGINS). Off by default
--log-level <level>    Trace|Debug|Information|Warning|Error
--help, --version
```

and exposes a `server_info` tool that reports its version, transport and effective configuration (roots, store, flags) so an agent can check what it is talking to.

## Where data lives

SQLite stores and dry-run proposals live under `~/.mcp-services/<server>/` (`MCP_SERVICES_HOME` overrides). Delete a server's folder to reset it. With `--store postgres:<connection string>` nothing is written locally.

## Client cheat sheet

| Client | File | Shape |
|---|---|---|
| Cursor | `.cursor/mcp.json` or `~/.cursor/mcp.json` | `mcpServers` → `command`/`args`/`env` or `url` + optional `headers` |
| Claude Desktop | `claude_desktop_config.json` | same, absolute `command` paths |
| VS Code | `.vscode/mcp.json` | `servers` → `type: stdio` + `command`, supports `${input:...}` prompts |
| Grok Build | `~/.grok/config.toml` or `.grok/config.toml` | stdio `command`/`args`; see [examples/grok.config.toml](../examples/grok.config.toml) |
| Grok Bot | none (chat) | public `https://…/mcp` + `Authorization: Bearer`; see [examples/grok-bot.md](../examples/grok-bot.md) |
| agentPacks plugin | `plugins/<plugin>/mcp.json` | Agent Plugins 1.0.0: `type: stdio`, bare `command`, `${PLUGIN_ROOT}`/`${PLUGIN_DATA}` only in `args`/`env`/`cwd`, no credential-like env keys |

Copy-ready files for each are in `examples/`.

### agentPacks specifics

`examples/agentpacks.mcp.json` validates against the Agent Plugins 1.0.0 schema used by [svennijhuis/agentPacks](https://github.com/svennijhuis/agentPacks): commands are bare tokens resolved on `PATH` (so install the global tools first), all configuration is in `args`/`env`, and secrets are never in the file. `mcp-learnings` reads `CURSOR_API_KEY` from the process environment; `mcp-index` reads `OPENAI_API_KEY` the same way. Paths for the servers to work on are passed by the agent to the tools (`load_solution`, `index_repository`, ...) or expressed with `${PLUGIN_DATA}`; do not use `--restrict` there unless the workspace lives under the plugin data directory. Adding real servers to agentPacks also means updating its `PluginMcpContractTests`, which currently assert that every `mcp.json` is an empty scaffold.

## Troubleshooting

- **"You must install .NET to run this application"** when starting a tool shim: set `DOTNET_ROOT` (see above).
- **Client shows no tools**: run the command in a terminal with the same args; the server prints its usage and errors to stderr and exits with code 2 on a start-up problem (missing directory, bad connection string).
- **Roslyn: "No .NET SDK found by MSBuildLocator"**: `dotnet` must be on `PATH` of the server process, or set `DOTNET_ROOT`. `list_workspaces` reports the MSBuild status.
- **Roslyn: projects load with warnings / missing references**: run `dotnet restore` on the solution once; `workspace_status` shows `loadDiagnostics`.
- **HTTP 401 / Grok Bot reports no tools**: `/mcp` requires `Authorization: Bearer` when `MCP_AUTH_TOKEN` is set (always, in Docker). Health endpoints do not. Confirm the URL path is `/mcp`, not `/health`.
- **Tunnel Host header rejected (400 Bad Request)**: set `--allowed-host <hostname>` or `MCP_ALLOWED_HOSTS=*`, and `--public-url https://<hostname>`.
- **Grok Bot cannot reach localhost**: the bot runs in the cloud. Use a public HTTPS tunnel; do not paste a `command`/`npx` line for these .NET servers. Team MCP allowlists can also block unknown URLs.
- **Index looks stale**: `index_status` shows `stale` with the changed files; `verify_index` with `repair=true` fixes inconsistencies; the git hooks (`scripts/install-git-hooks.sh`) keep it warm outside MCP sessions.
