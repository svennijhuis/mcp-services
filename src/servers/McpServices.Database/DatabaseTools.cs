using System.ComponentModel;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using McpServices.Database.Providers;
using McpServices.Hosting;
using ModelContextProtocol.Server;

namespace McpServices.Database;

/// <summary>Read-only tools, always registered.</summary>
[McpServerToolType]
public sealed class DatabaseReadTools(DatabaseRegistry registry)
{
    public const int DefaultMaxRows = 200;
    public const int HardMaxRows = 5000;

    [McpServerTool(Name = "list_databases", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "List databases")]
    [Description("List the registered database aliases with their engine and a redacted connection string. Pass an alias as 'database' to the other tools.")]
    public async Task<object> ListDatabases(CancellationToken cancellationToken = default)
    {
        var items = new List<object>();
        foreach (var provider in registry.All)
        {
            string status;
            try
            {
                await provider.PingAsync(cancellationToken).ConfigureAwait(false);
                status = "reachable";
            }
            catch (DbException ex)
            {
                status = "unreachable: " + ex.Message;
            }

            items.Add(new { alias = provider.Alias, engine = provider.Kind, connection = provider.RedactedConnectionString, status });
        }

        return new { readOnly = registry.ReadOnly, databases = items };
    }

    [McpServerTool(Name = "list_tables", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "List tables")]
    [Description("List tables and views in a database, optionally filtered by a case-insensitive substring.")]
    public async Task<object> ListTables(
        [Description("Database alias (optional when only one is registered).")] string? database = null,
        [Description("Only return tables whose name contains this text.")] string? search = null,
        [Description("Page token from a previous call.")] string? pageToken = null,
        [Description("Page size (default 50, max 500).")] int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        var provider = registry.Get(database);
        var tables = await Run(() => provider.ListTablesAsync(cancellationToken)).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(search))
        {
            tables = tables.Where(t => t.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || (t.Schema?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        }

        var page = Paging.Page(tables, pageToken, pageSize);
        return new { database = provider.Alias, total = page.TotalCount, nextPageToken = page.NextPageToken, tables = page.Items };
    }

    [McpServerTool(Name = "describe_table", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Describe table")]
    [Description("Columns (type, nullability, default, primary key), foreign keys and indexes of a table. Use 'schema.table' or the schema parameter for non-default schemas.")]
    public async Task<TableSchema> DescribeTable(
        [Description("Table name, optionally schema-qualified.")] string table,
        [Description("Database alias.")] string? database = null,
        [Description("Schema (default: public / dbo).")] string? schema = null,
        CancellationToken cancellationToken = default)
    {
        var provider = registry.Get(database);
        return await Run(() => provider.DescribeTableAsync(table, schema, cancellationToken)).ConfigureAwait(false);
    }

    [McpServerTool(Name = "get_schema_overview", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Schema overview")]
    [Description("Compact overview of every table with its columns, useful as a first orientation before writing queries. Limited to 100 tables.")]
    public async Task<object> GetSchemaOverview(
        [Description("Database alias.")] string? database = null,
        CancellationToken cancellationToken = default)
    {
        var provider = registry.Get(database);
        var tables = await Run(() => provider.ListTablesAsync(cancellationToken)).ConfigureAwait(false);
        var overview = new List<object>();
        foreach (var table in tables.Take(100))
        {
            var qualified = table.Schema is null ? table.Name : $"{table.Schema}.{table.Name}";
            try
            {
                var schema = await provider.DescribeTableAsync(qualified, table.Schema, cancellationToken).ConfigureAwait(false);
                overview.Add(new
                {
                    table = qualified,
                    type = table.Type,
                    estimatedRows = table.EstimatedRows,
                    columns = schema.Columns.Select(c => $"{c.Name} {c.DataType}{(c.PrimaryKey ? " PK" : string.Empty)}{(c.Nullable ? string.Empty : " NOT NULL")}").ToList(),
                    foreignKeys = schema.ForeignKeys.Select(f => $"{f.Column} -> {f.ReferencedTable}.{f.ReferencedColumn}").ToList(),
                });
            }
            catch (Exception ex) when (ex is DbException or ToolException)
            {
                overview.Add(new { table = qualified, type = table.Type, error = ex.Message });
            }
        }

        return new { database = provider.Alias, engine = provider.Kind, tableCount = tables.Count, truncated = tables.Count > 100, tables = overview };
    }

    [McpServerTool(Name = "read_query", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Read query")]
    [Description("Run a single read-only SQL statement (SELECT, WITH ... SELECT, EXPLAIN, PRAGMA, SHOW). Use @name placeholders with the 'params' object instead of string concatenation. Results are capped by maxRows (default 200).")]
    public async Task<object> ReadQuery(
        [Description("SQL statement.")] string sql,
        [Description("Database alias.")] string? database = null,
        [Description("Named parameters referenced as @name in the SQL.")] Dictionary<string, object?>? @params = null,
        [Description("Maximum rows to return (default 200, max 5000).")] int? maxRows = null,
        CancellationToken cancellationToken = default)
    {
        var provider = registry.Get(database);
        SqlGuard.EnsureRead(sql);
        var limit = Math.Clamp(maxRows ?? DefaultMaxRows, 1, HardMaxRows);
        var result = await Run(() => provider.ExecuteReadAsync(sql, @params, limit, cancellationToken)).ConfigureAwait(false);
        return Shape(provider.Alias, result);
    }

    [McpServerTool(Name = "explain_query", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Explain query")]
    [Description("Show the engine's execution plan for a read-only statement without running it against the data (EXPLAIN QUERY PLAN / EXPLAIN / SHOWPLAN).")]
    public async Task<object> ExplainQuery(
        [Description("SQL statement.")] string sql,
        [Description("Database alias.")] string? database = null,
        [Description("Named parameters.")] Dictionary<string, object?>? @params = null,
        CancellationToken cancellationToken = default)
    {
        var provider = registry.Get(database);
        var plan = await Run(() => provider.ExplainAsync(sql, @params, cancellationToken)).ConfigureAwait(false);
        return new { database = provider.Alias, engine = provider.Kind, plan };
    }

    [McpServerTool(Name = "export_query", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Export query")]
    [Description("Run a read-only query and return the full result as CSV or JSON text (up to 5000 rows).")]
    public async Task<string> ExportQuery(
        [Description("SQL statement.")] string sql,
        [Description("Output format: csv or json (default csv).")] string format = "csv",
        [Description("Database alias.")] string? database = null,
        [Description("Named parameters.")] Dictionary<string, object?>? @params = null,
        [Description("Maximum rows (default 5000).")] int? maxRows = null,
        CancellationToken cancellationToken = default)
    {
        var provider = registry.Get(database);
        SqlGuard.EnsureRead(sql);
        var limit = Math.Clamp(maxRows ?? HardMaxRows, 1, HardMaxRows);
        var result = await Run(() => provider.ExecuteReadAsync(sql, @params, limit, cancellationToken)).ConfigureAwait(false);

        return format.ToLowerInvariant() switch
        {
            "csv" => ToCsv(result),
            "json" => ToolJson.Serialize(result.Rows.Select(r => r.Select((v, i) => (result.Columns[i], v)).ToDictionary(t => t.Item1, t => t.v))),
            _ => throw new ToolException("format must be 'csv' or 'json'."),
        };
    }

    internal static object Shape(string alias, QueryResult result) => new
    {
        database = alias,
        columns = result.Columns.Select((c, i) => new { name = c, type = result.ColumnTypes[i] }),
        rowCount = result.Rows.Count,
        truncated = result.Truncated,
        elapsedMs = result.ElapsedMs,
        rows = result.Rows,
    };

    internal static async Task<T> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            throw new ToolException($"Database error: {ex.Message}", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new ToolException($"Database error: {ex.Message}", ex);
        }
    }

    private static string ToCsv(QueryResult result)
    {
        var sb = new StringBuilder();
        sb.AppendJoin(',', result.Columns.Select(Escape)).Append('\n');
        foreach (var row in result.Rows)
        {
            sb.AppendJoin(',', row.Select(v => Escape(Format(v)))).Append('\n');
        }

        return sb.ToString();

        static string Format(object? value) => value switch
        {
            null => string.Empty,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            JsonElement je => je.GetRawText(),
            _ => value.ToString() ?? string.Empty,
        };

        static string Escape(string value) =>
            value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal)
                ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
                : value;
    }
}

/// <summary>Write tools, registered only when the server is not started with --read-only.</summary>
[McpServerToolType]
public sealed class DatabaseWriteTools(DatabaseRegistry registry)
{
    [McpServerTool(Name = "write_query", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, Title = "Write query")]
    [Description("Run a single data- or schema-modifying statement (INSERT, UPDATE, DELETE, CREATE, ALTER, DROP ...). Use @name parameters. Returns the affected row count. Not available when the server runs with --read-only.")]
    public async Task<object> WriteQuery(
        [Description("SQL statement.")] string sql,
        [Description("Database alias.")] string? database = null,
        [Description("Named parameters referenced as @name in the SQL.")] Dictionary<string, object?>? @params = null,
        CancellationToken cancellationToken = default)
    {
        var provider = registry.Get(database);
        var kind = SqlGuard.Classify(sql);
        if (kind == SqlKind.Read)
        {
            throw new ToolException("write_query received a read-only statement; use read_query instead.");
        }

        var result = await DatabaseReadTools.Run(() => provider.ExecuteWriteAsync(sql, @params, cancellationToken)).ConfigureAwait(false);
        return new { database = provider.Alias, affectedRows = result.AffectedRows, elapsedMs = result.ElapsedMs };
    }
}

[McpServerResourceType]
public sealed class DatabaseResources(DatabaseRegistry registry)
{
    [McpServerResource(UriTemplate = "db://{alias}/schema", Name = "schema", MimeType = "application/json", Title = "Database schema")]
    [Description("Full schema (tables, columns, foreign keys, indexes) of a registered database as JSON.")]
    public async Task<string> Schema(string alias, CancellationToken cancellationToken = default)
    {
        var provider = registry.Get(alias);
        var tables = await DatabaseReadTools.Run(() => provider.ListTablesAsync(cancellationToken)).ConfigureAwait(false);
        var schemas = new List<TableSchema>();
        foreach (var table in tables)
        {
            var qualified = table.Schema is null ? table.Name : $"{table.Schema}.{table.Name}";
            schemas.Add(await provider.DescribeTableAsync(qualified, table.Schema, cancellationToken).ConfigureAwait(false));
        }

        return ToolJson.Serialize(new { database = provider.Alias, engine = provider.Kind, tables = schemas });
    }
}
