using System.Data.Common;
using System.Globalization;
using System.Text;
using McpServices.Hosting;
using Microsoft.Data.Sqlite;

namespace McpServices.Database.Providers;

public sealed class SqliteProvider : AdoNetProvider
{
    public SqliteProvider(string alias, string connectionString)
        : base(alias, NormalizeConnectionString(connectionString))
    {
    }

    public override string Kind => "sqlite";

    public override DbConnection CreateConnection() => new SqliteConnection(ConnectionString);

    // Microsoft.Data.Sqlite pools connections, so query_only must be set and reset explicitly per operation.
    protected override Task PrepareReadConnectionAsync(DbConnection connection, CancellationToken cancellationToken) =>
        SetQueryOnlyAsync(connection, enabled: true, cancellationToken);

    protected override Task PrepareWriteConnectionAsync(DbConnection connection, CancellationToken cancellationToken) =>
        SetQueryOnlyAsync(connection, enabled: false, cancellationToken);

    // A deferred BEGIN takes no write lock; BEGIN IMMEDIATE (the SDK default for Serializable) is refused by query_only.
    protected override ValueTask<DbTransaction> BeginReadTransactionAsync(DbConnection connection, CancellationToken cancellationToken) =>
        new(((SqliteConnection)connection).BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: true));

    private static async Task SetQueryOnlyAsync(DbConnection connection, bool enabled, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = enabled ? "PRAGMA query_only = 1;" : "PRAGMA query_only = 0;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<TableInfo>> ListTablesAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var result = await QueryAsync(connection, "SELECT name, type FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' ORDER BY type, name", null, cancellationToken).ConfigureAwait(false);
        return result.Rows.Select(r => new TableInfo(null, (string)r[0]!, (string)r[1]!, null)).ToList();
    }

    public override async Task<TableSchema> DescribeTableAsync(string table, string? schema, CancellationToken cancellationToken)
    {
        SqlGuard.ValidateIdentifier(table, "table");
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var exists = await QueryAsync(connection, "SELECT 1 FROM sqlite_master WHERE name = @name AND type IN ('table','view')", new Dictionary<string, object?> { ["name"] = table }, cancellationToken).ConfigureAwait(false);
        if (exists.Rows.Count == 0)
        {
            throw new ToolException($"Table '{table}' does not exist in database '{Alias}'.");
        }

        var quoted = Quote(table);
        var columns = await QueryAsync(connection, $"PRAGMA table_info({quoted})", null, cancellationToken).ConfigureAwait(false);
        var fks = await QueryAsync(connection, $"PRAGMA foreign_key_list({quoted})", null, cancellationToken).ConfigureAwait(false);
        var indexes = await QueryAsync(connection, $"PRAGMA index_list({quoted})", null, cancellationToken).ConfigureAwait(false);

        var columnInfos = columns.Rows.Select(r => new ColumnInfo(
            Name: (string)r[1]!,
            DataType: r[2] as string ?? "ANY",
            Nullable: Convert.ToInt64(r[3], CultureInfo.InvariantCulture) == 0,
            DefaultValue: r[4]?.ToString(),
            PrimaryKey: Convert.ToInt64(r[5], CultureInfo.InvariantCulture) > 0,
            Ordinal: (int)Convert.ToInt64(r[0], CultureInfo.InvariantCulture))).ToList();

        var fkInfos = fks.Rows.Select(r => new ForeignKeyInfo(
            Name: $"fk_{r[0]}",
            Column: (string)r[3]!,
            ReferencedTable: (string)r[2]!,
            ReferencedColumn: r[4] as string ?? "rowid")).ToList();

        var indexInfos = new List<IndexInfo>();
        foreach (var idx in indexes.Rows)
        {
            var name = (string)idx[1]!;
            var cols = await QueryAsync(connection, $"PRAGMA index_info({Quote(name)})", null, cancellationToken).ConfigureAwait(false);
            indexInfos.Add(new IndexInfo(name, Convert.ToInt64(idx[2], CultureInfo.InvariantCulture) == 1, cols.Rows.Select(c => c[2] as string ?? "<expr>").ToList()));
        }

        return new TableSchema(null, table, columnInfos, fkInfos, indexInfos);
    }

    public override async Task<string> ExplainAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        SqlGuard.EnsureRead(sql);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var result = await QueryAsync(connection, "EXPLAIN QUERY PLAN " + sql, parameters, cancellationToken).ConfigureAwait(false);
        var sb = new StringBuilder();
        foreach (var row in result.Rows)
        {
            sb.Append(CultureInfo.InvariantCulture, $"id={row[0]} parent={row[1]}: {row[3]}").Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string NormalizeConnectionString(string value)
    {
        // Accept a bare file path as a convenience: "sqlite:./data/app.db".
        if (!value.Contains('=', StringComparison.Ordinal))
        {
            return new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(value), Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true }.ToString();
        }

        var builder = new SqliteConnectionStringBuilder(value);
        if (!string.IsNullOrEmpty(builder.DataSource) && builder.DataSource != ":memory:" && !builder.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            builder.DataSource = Path.GetFullPath(builder.DataSource);
        }

        return builder.ToString();
    }
}
