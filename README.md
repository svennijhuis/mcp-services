# mcp-services

Model Context Protocol (MCP) services, written in TypeScript on top of the
official [`@modelcontextprotocol/sdk`](https://github.com/modelcontextprotocol/typescript-sdk).

The repository ships a small but complete MCP server that speaks JSON-RPC over
stdio and exposes a few example tools plus a resource. It is a starting point
for building real MCP services.

## Requirements

- Node.js >= 20 (developed against Node 22)
- npm

## Getting started

```bash
npm ci        # install dependencies
npm run build # compile TypeScript to dist/
npm test      # run the test suite
npm run demo  # end-to-end demo against the built server
```

## Server capabilities

The server (`src/server.ts`) registers:

| Kind     | Name          | Description                                   |
| -------- | ------------- | --------------------------------------------- |
| tool     | `add`         | Add two numbers and return their sum.         |
| tool     | `echo`        | Return the provided message unchanged.        |
| tool     | `now`         | Return the current server time (ISO-8601).    |
| resource | `server-info` | Basic metadata about the server (`info://server`). |

## Running the server

`npm run dev` runs the server in watch mode over stdio. For a production-style
run, build first and start the compiled entrypoint:

```bash
npm run build
npm start
```

The process communicates using the MCP JSON-RPC protocol on stdin/stdout, so it
is meant to be launched by an MCP host (an IDE, an agent, or the demo client)
rather than used interactively.

### Connecting from an MCP host

Point any MCP-compatible host at the built entrypoint:

```json
{
  "mcpServers": {
    "mcp-services": {
      "command": "node",
      "args": ["/absolute/path/to/mcp-services/dist/index.js"]
    }
  }
}
```

## Scripts

| Script              | Purpose                                             |
| ------------------- | --------------------------------------------------- |
| `npm run build`     | Compile `src/` to `dist/`.                          |
| `npm run dev`       | Run the server over stdio in watch mode.            |
| `npm start`         | Run the compiled server (`dist/index.js`).          |
| `npm run demo`      | Spawn the built server and exercise it as a client. |
| `npm test`          | Run the Vitest suite.                               |
| `npm run typecheck` | Type-check without emitting.                        |
| `npm run lint`      | Lint with ESLint.                                   |

## Project layout

```
src/
  server.ts   # createServer(): registers tools + resources (transport-agnostic)
  index.ts    # stdio entrypoint
test/
  server.test.ts  # in-memory client <-> server integration tests
scripts/
  demo.ts     # end-to-end stdio demo client
```
