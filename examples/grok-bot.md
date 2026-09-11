# Connecting Grok Bot to mcp-services

Three xAI products, three setups — do not mix them:

| Product | What it is | How you add these servers |
|---|---|---|
| **Grok Build** | Terminal coding agent | Local stdio via `grok mcp add` or [grok.config.toml](grok.config.toml). Can also merge `.cursor/mcp.json`. |
| **Grok Bot** | Personal agents in the Grok / Cursor bot app | Remote **HTTPS** Streamable HTTP URL at `/mcp`, added **in chat**. Ignores `.cursor/mcp.json`. |
| **grok.com chat** | grok.com → Connectors | Browser UI; needs opt-in CORS (`--cors-origin`). Not the default path. |

Grok Bot runs on a cloud computer. `http://localhost:5100/mcp` never works. `npx mcp-roslyn` / .NET global tools also do not work there: their VM is not this repo and typically has no .NET 10 SDK. Host the servers yourself and give the bot a public URL.

## Local first, then a tunnel

Keep using Cursor / Grok Build over **stdio** on your machine. For Grok Bot, expose the same binaries over Streamable HTTP and put a real HTTPS certificate in front (self-signed certs fail).

```bash
# 1. Token is mandatory for any public URL — including a tunnel to loopback.
#    Binding 127.0.0.1 is not a security boundary once a tunnel exists.
openssl rand -hex 32
export MCP_AUTH_TOKEN=<that value>

# 2a. Docker stack (shared Postgres for index + learnings — required if Cursor and Grok Bot share knowledge)
cp docker/.env.example docker/.env   # paste MCP_AUTH_TOKEN
cd docker && WORKSPACE=/abs/repo docker compose up -d --build

# 2b. Or one server from a global tool / published binary (loopback, still needs the token for Grok)
mcp-index --http --port 5103 --host 127.0.0.1 --auth-token "$MCP_AUTH_TOKEN" --root /abs/repo

# 3. Public HTTPS in front of loopback. cloudflared gives a valid certificate.
#    Repeat per port you want the bot to use (5100 filesystem … 5104 learnings).
cloudflared tunnel --url http://127.0.0.1:5103
```

If the Host header of the tunnel is rejected, pass `--allowed-host <tunnel-hostname>` or `MCP_ALLOWED_HOSTS=*` and `--public-url https://<tunnel-host>` (env `MCP_PUBLIC_URL`) so `server_info` advertises the HTTPS URL instead of `http://0.0.0.0:5100/mcp`.

## Chat lines (say “custom server”)

Without the words **custom server**, Grok Bot may look for a marketplace plugin. Confirm the URL is `https://…/mcp` — not `/`, `/sse`, or `/health`. After the bot stores the header, do not paste the token again.

```
Add a custom MCP server called filesystem at https://<host>/mcp with header Authorization: Bearer <token>
Add a custom MCP server called roslyn at https://<host>/mcp with header Authorization: Bearer <token>
Add a custom MCP server called database at https://<host>/mcp with header Authorization: Bearer <token>
Add a custom MCP server called index at https://<host>/mcp with header Authorization: Bearer <token>
Add a custom MCP server called learnings at https://<host>/mcp with header Authorization: Bearer <token>
```

Then attach a server with `@` in the chat. Plugins are account-wide. If a team admin has an MCP allowlist, the tunnel URL must be on it.

Health probes (`GET /health` and `GET /healthz`) stay unauthenticated for load balancers. Do not point Grok Bot at those paths.

## Guard rails on a public URL

- **Token.** Anyone who has it can write files, apply Roslyn refactors, and dispatch Learnings proposals. Rotate it if it leaks. Never put it in a URL or in example JSON committed to git.
- **Docker defaults stay conservative:** database `--read-only`, Roslyn `--no-scripting --restrict`, compose ports on `127.0.0.1`. Do not publish `0.0.0.0` on the host.
- **Do not** set `--dispatch auto` on a URL the bot can reach. Keep Learnings at `--dispatch manual` (compose default).
- **Postgres, not SQLite**, when more than one agent (Cursor + Grok Bot, or several bots) shares index/learnings. SQLite will lock and diverge.
- Opt-in CORS (`--cors-origin` / `MCP_CORS_ORIGINS`) is only for browser clients such as grok.com. Grok Bot does not need it.

`server_info` reports `authRequired: true` and never echoes the token.
