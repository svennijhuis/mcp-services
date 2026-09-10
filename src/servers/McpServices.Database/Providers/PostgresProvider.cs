using System.Data.Common;
using System.Globalization;
using McpServices.Hosting;
using Npgsql;

namespace McpServices.Database.Providers;

public sealed class PostgresProvider(string alias, string connectionString) : AdoNetProvider(alias, connectionString)
{
    private const string TableSql = """
        SELECT n.nspname AS schema, c.relname AS name,
               CASE c.relkind WHEN 'r' THEN 'table' WHEN 'p' THEN 'table' WHEN 'v' THEN 'view' WHEN 'm' THEN 'materialized view' WHEN 'f' THEN 'foreign table' END AS type,
               c.reltuples::bigint AS estimated_rows
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('r','p','v','m','f')
          AND n.nspname NOT IN ('pg_catalog','information_schema','pg_toast')
          AND n.nspname NOT LIKE 'pg_temp%'
        ORDER BY n.nspname, c.relname
        """;

    private const string ColumnSql = """
        SELECT c.column_name, c.data_type, c.udt_name, c.is_nullable = 'YES', c.column_default, c.ordinal_position,
               EXISTS (
                 SELECT 1 FROM information_schema.table_constraints tc
                 JOIN information_schema.key_column_usage k ON k.constraint_name = tc.constraint_name AND k.table_schema = tc.table_schema
                 WHERE tc.constraint_type = 'PRIMARY KEY' AND tc.table_schema = c.table_schema AND tc.table_name = c.table_name AND k.column_name = c.column_name
               ) AS is_pk
        FROM information_schema.columns c
        WHERE c.table_schema = @schema AND c.table_name = @table
        ORDER BY c.ordinal_position
        """;

    private const string ForeignKeySql = """
        SELECT tc.constraint_name, kcu.column_name, ccu.table_schema || '.' || ccu.table_name, ccu.column_name
        FROM information_schema.table_constraints tc
        JOIN information_schema.key_column_usage kcu ON kcu.constraint_name = tc.constraint_name AND kcu.table_schema = tc.table_schema
        JOIN information_schema.constraint_column_usage ccu ON ccu.constraint_name = tc.constraint_name AND ccu.table_schema = tc.table_schema
        WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_schema = @schema AND tc.table_name = @table
        ORDER BY tc.constraint_name, kcu.ordinal_position
        """;

    private const string IndexSql = """
        SELECT i.relname AS index_name, ix.indisunique, a.attname
        FROM pg_class t
        JOIN pg_namespace n ON n.oid = t.relnamespace
        JOIN pg_index ix ON ix.indrelid = t.oid
        JOIN pg_class i ON i.oid = ix.indexrelid
        JOIN unnest(ix.indkey) WITH ORDINALITY AS k(attnum, ord) ON true
        JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
        WHERE n.nspname = @schema AND t.relname = @table
        ORDER BY i.relname, k.ord
        """;

    public override string Kind => "postgres";

    public override DbConnection CreateConnection() => new NpgsqlConnection(ConnectionString);

    protected override async Task PrepareReadConnectionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SET default_transaction_read_only = on;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<TableInfo>> ListTablesAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var result = await QueryAsync(connection, TableSql, null, cancellationToken).ConfigureAwait(false);
        return result.Rows.Select(r => new TableInfo((string)r[0]!, (string)r[1]!, (string)r[2]!, r[3] is long l && l >= 0 ? l : null)).ToList();
    }

    public override async Task<TableSchema> DescribeTableAsync(string table, string? schema, CancellationToken cancellationToken)
    {
        (schema, table) = SplitSchema(table, schema ?? "public");
        var parameters = new Dictionary<string, object?> { ["schema"] = schema, ["table"] = table };

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var columns = await QueryAsync(connection, ColumnSql, parameters, cancellationToken).ConfigureAwait(false);
        if (columns.Rows.Count == 0)
        {
            throw new ToolException($"Table '{schema}.{table}' does not exist in database '{Alias}'.");
        }

        var fks = await QueryAsync(connection, ForeignKeySql, parameters, cancellationToken).ConfigureAwait(false);
        var indexes = await QueryAsync(connection, IndexSql, parameters, cancellationToken).ConfigureAwait(false);

        var columnInfos = columns.Rows.Select(r => new ColumnInfo(
            Name: (string)r[0]!,
            DataType: (string)r[1]! is "USER-DEFINED" or "ARRAY" ? (string)r[2]! : (string)r[1]!,
            Nullable: (bool)r[3]!,
            DefaultValue: r[4]?.ToString(),
            PrimaryKey: (bool)r[6]!,
            Ordinal: Convert.ToInt32(r[5], CultureInfo.InvariantCulture))).ToList();

        var fkInfos = fks.Rows.Select(r => new ForeignKeyInfo((string)r[0]!, (string)r[1]!, (string)r[2]!, (string)r[3]!)).ToList();
        var indexInfos = indexes.Rows
            .GroupBy(r => ((string)r[0]!, (bool)r[1]!))
            .Select(g => new IndexInfo(g.Key.Item1, g.Key.Item2, g.Select(r => (string)r[2]!).ToList()))
            .ToList();

        return new TableSchema(schema, table, columnInfos, fkInfos, indexInfos);
    }

    public override async Task<string> ExplainAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        SqlGuard.EnsureRead(sql);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var result = await QueryAsync(connection, "EXPLAIN (FORMAT TEXT) " + sql, parameters, cancellationToken).ConfigureAwait(false);
        return string.Join('\n', result.Rows.Select(r => r[0]?.ToString()));
    }

    internal static (string Schema, string Table) SplitSchema(string table, string defaultSchema)
    {
        SqlGuard.ValidateIdentifier(table, "table");
        var dot = table.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? (table[..dot], table[(dot + 1)..]) : (defaultSchema, table);
    }
}
