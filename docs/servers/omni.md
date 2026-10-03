# mcp-omni

One stdio process the client imports instead of five. It speaks MCP on stdin and calls the existing HTTP backends one at a time. The client does not start filesystem, Roslyn, database, index, or learnings itself.

Project: `src/servers/McpServices.Omni` · Tests: `tests/McpServices.Omni.Tests` · Diagrams: [docs/omni-design.md](../omni-design.md)

Diagrams 14 and 15 in that file are this local build. Diagrams 1 to 13 are the later APIM gateway and are docs only. This process does not implement APIM, Azure, or a JWT check. APIM is later: [infrastructure-azure-white-label#9](https://github.com/svennijhuis/infrastructure-azure-white-label/pull/9).

## What the client gets

Four tools:

| Tool | What it returns |
| --- | --- |
| `discover_servers` | Enabled servers only: id, title, summary. |
| `discover_tools` | One server, or every enabled server. Names and one line. No schemas. |
| `get_tool_schema` | One tool: name, description, inputSchema. Not a dump of every tool. |
| `invoke_tool` | Forwards arguments, then truncates the text to that server's maxChars (8000 in the checked-in registry). |

The shared host also exposes `server_info`, the same way it does for every other server. Omni does not re-export filesystem, SQL, or Roslyn tools.

Pass a server id from `discover_servers`. A URL argument is rejected. Omni does not send a bearer token or a user JWT. A disabled id, an unknown id, a down backend, or a timeout is a tool error. It is not an HTTP 401, and the message is not a socket exception.

Each backend is initialized once (protocol `2025-06-18`), then Omni calls `tools/list` or `tools/call`. `role` in the registry is documentation and is not enforced.

## Registry

`registry.json` next to the executable lists the five backends. Every entry defaults to `enabled: false`, `projection: summary`, and `maxChars: 8000`. The checked-in file uses compose DNS (`http://filesystem:5100/mcp` and the same shape for roslyn, database, index, and learnings).

Laptop prove uses loopback. Copy [examples/omni.localhost.registry.json](../../examples/omni.localhost.registry.json) and point the process at it:

```json
{
  "mcpServers": {
    "omni": {
      "command": "mcp-omni",
      "env": { "MCP_OMNI_REGISTRY": "/absolute/path/omni.localhost.registry.json" }
    }
  }
}
```

That sample uses `http://127.0.0.1:5100/mcp` through `5104` (filesystem, roslyn, database, index, learnings) and turns those entries on. `--registry` is the same override as `MCP_OMNI_REGISTRY`.

```
mcp-omni [transport options] [--registry <path>] [--timeout <seconds>]
```

Start the five HTTP servers first (`cd docker && docker compose up -d --build`, or run each with `--http` on ports 5100-5104). Omni itself stays on stdio unless you pass `--http`.
