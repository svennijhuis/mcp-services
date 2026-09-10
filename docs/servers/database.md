# mcp-database

Schema discovery and SQL access to SQLite, PostgreSQL and SQL Server through one tool surface. Statements are validated by `SqlGuard` (single statement, read-only classification) before they reach the driver, and parameters are always bound (`@name`), never concatenated.

Project: `src/servers/McpServices.Database` · Tests: `tests/McpServices.Database.Tests`

## Running

```
mcp-database [transport options] [--read-only] --db <alias>=<provider>:<connection-string> [--db ...]
```

| Provider | Connection string |
| --- | --- |
| `sqlite` | File path (`sqlite:/data/app.db`) or a `Microsoft.Data.Sqlite` connection string. |
| `postgres` | Npgsql string, e.g. `Host=localhost;Database=app;Username=app;Password=...`. |
| `sqlserver` | `Microsoft.Data.SqlClient` string. |

| Setting | How |
| --- | --- |
| Register databases | `--db alias=provider:conn` (repeatable) or env `MCP_DB__<alias>=provider:conn` |
| Read-only mode | `--read-only` or `MCP_DB_READ_ONLY=true` — hides `write_query` and rejects every modifying statement |

Keep credentials in the environment (`MCP_DB__main=postgres:Host=...;Password=...`), not in an `mcp.json` that gets committed. `list_databases` redacts passwords when it echoes connection strings.

## Tools

| Tool | Purpose |
| --- | --- |
| `list_databases` | Aliases, engine and redacted connection string. |
| `list_tables` | Tables and views, optional substring filter. |
| `describe_table` | Columns (type, nullability, default, PK), foreign keys, indexes. Accepts `schema.table`. |
| `get_schema_overview` | Every table with its columns in one compact answer (max 100 tables). |
| `read_query` | Single read-only statement with `params`; rows capped by `maxRows` (default 200, max 5000). |
| `explain_query` | Execution plan (`EXPLAIN QUERY PLAN` / `EXPLAIN` / `SHOWPLAN`) without running the query. |
| `export_query` | Full result as CSV or JSON text, up to 5000 rows. |
| `write_query` | INSERT/UPDATE/DELETE/DDL with parameters; returns affected rows. Absent when read-only. |

Resource: `db://{alias}/schema` — the schema overview as a resource, handy for clients that attach resources to a chat.

## Client configuration

```json
{
  "mcpServers": {
    "database": {
      "command": "mcp-database",
      "args": ["--db", "main=sqlite:/home/sven/data/app.db", "--read-only"]
    }
  }
}
```

Docker: the `database` service in `docker/docker-compose.yml` connects to the bundled `postgres` (pgvector) container read-only via `MCP_DB__mcp` on `http://127.0.0.1:5102/mcp`.

## Guard rails

- Exactly one statement per call; a second statement (even after a `;`) is rejected.
- `read_query`/`explain_query`/`export_query` accept only SELECT, `WITH ... SELECT`, EXPLAIN, PRAGMA and SHOW. Anything else must go through `write_query`.
- SQLite `PRAGMA` writes (e.g. `PRAGMA journal_mode = WAL`) are treated as writes.
- Query results include column names and engine types, so agents can reason about the data without an extra `describe_table`.
