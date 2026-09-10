using System.Data.Common;
using System.Globalization;
using McpServices.Hosting;
using Microsoft.Data.SqlClient;

namespace McpServices.Database.Providers;

public sealed class SqlServerProvider(string alias, string connectionString) : AdoNetProvider(alias, connectionString)
{
    private const string TableSql = """
        SELECT s.name AS [schema], o.name, CASE o.type WHEN 'U' THEN 'table' WHEN 'V' THEN 'view' END AS type,
               (SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = o.object_id AND p.index_id IN (0,1)) AS estimated_rows
        FROM sys.objects o
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        WHERE o.type IN ('U','V') AND o.is_ms_shipped = 0
        ORDER BY s.name, o.name
        """;

    private const string ColumnSql = """
        SELECT c.name, t.name AS data_type, c.max_length, c.precision, c.scale, c.is_nullable, dc.definition, c.column_id,
               CASE WHEN EXISTS (
                 SELECT 1 FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                 WHERE i.is_primary_key = 1 AND ic.object_id = c.object_id AND ic.column_id = c.column_id) THEN 1 ELSE 0 END AS is_pk
        FROM sys.columns c
        JOIN sys.types t ON t.user_type_id = c.user_type_id
        LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
        WHERE c.object_id = OBJECT_ID(@qualified)
        ORDER BY c.column_id
        """;

    private const string ForeignKeySql = """
        SELECT fk.name, pc.name, SCHEMA_NAME(rt.schema_id) + '.' + rt.name, rc.name
        FROM sys.foreign_keys fk
        JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
        JOIN sys.tables rt ON rt.object_id = fkc.referenced_object_id
        JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
        WHERE fk.parent_object_id = OBJECT_ID(@qualified)
        ORDER BY fk.name, fkc.constraint_column_id
        """;

    private const string IndexSql = """
        SELECT i.name, i.is_unique, c.name
        FROM sys.indexes i
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = OBJECT_ID(@qualified) AND i.name IS NOT NULL
        ORDER BY i.name, ic.key_ordinal
        """;

    public override string Kind => "sqlserver";

    public override DbConnection CreateConnection() => new SqlConnection(ConnectionString);

    public override async Task<IReadOnlyList<TableInfo>> ListTablesAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var result = await QueryAsync(connection, TableSql, null, cancellationToken).ConfigureAwait(false);
        return result.Rows.Select(r => new TableInfo((string)r[0]!, (string)r[1]!, (string)r[2]!, r[3] is null ? null : Convert.ToInt64(r[3], CultureInfo.InvariantCulture))).ToList();
    }

    public override async Task<TableSchema> DescribeTableAsync(string table, string? schema, CancellationToken cancellationToken)
    {
        var (resolvedSchema, name) = PostgresProvider.SplitSchema(table, schema ?? "dbo");
        var parameters = new Dictionary<string, object?> { ["qualified"] = $"[{resolvedSchema}].[{name}]" };

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var columns = await QueryAsync(connection, ColumnSql, parameters, cancellationToken).ConfigureAwait(false);
        if (columns.Rows.Count == 0)
        {
            throw new ToolException($"Table '{resolvedSchema}.{name}' does not exist in database '{Alias}'.");
        }

        var fks = await QueryAsync(connection, ForeignKeySql, parameters, cancellationToken).ConfigureAwait(false);
        var indexes = await QueryAsync(connection, IndexSql, parameters, cancellationToken).ConfigureAwait(false);

        var columnInfos = columns.Rows.Select(r => new ColumnInfo(
            Name: (string)r[0]!,
            DataType: FormatType((string)r[1]!, Convert.ToInt32(r[2], CultureInfo.InvariantCulture), Convert.ToInt32(r[3], CultureInfo.InvariantCulture), Convert.ToInt32(r[4], CultureInfo.InvariantCulture)),
            Nullable: (bool)r[5]!,
            DefaultValue: r[6]?.ToString(),
            PrimaryKey: Convert.ToInt32(r[8], CultureInfo.InvariantCulture) == 1,
            Ordinal: Convert.ToInt32(r[7], CultureInfo.InvariantCulture))).ToList();

        var fkInfos = fks.Rows.Select(r => new ForeignKeyInfo((string)r[0]!, (string)r[1]!, (string)r[2]!, (string)r[3]!)).ToList();
        var indexInfos = indexes.Rows
            .GroupBy(r => ((string)r[0]!, (bool)r[1]!))
            .Select(g => new IndexInfo(g.Key.Item1, g.Key.Item2, g.Select(r => (string)r[2]!).ToList()))
            .ToList();

        return new TableSchema(resolvedSchema, name, columnInfos, fkInfos, indexInfos);
    }

    public override async Task<string> ExplainAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        SqlGuard.EnsureRead(sql);
        EnsureNoParameters(parameters, "explain_query");
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var on = connection.CreateCommand())
        {
            on.CommandText = "SET SHOWPLAN_TEXT ON";
            await on.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var result = await QueryAsync(connection, sql, null, cancellationToken).ConfigureAwait(false);
        return string.Join('\n', result.Rows.Select(r => r[0]?.ToString()));
    }

    private static string FormatType(string type, int maxLength, int precision, int scale) => type switch
    {
        "nvarchar" or "nchar" => maxLength == -1 ? $"{type}(max)" : $"{type}({maxLength / 2})",
        "varchar" or "char" or "varbinary" or "binary" => maxLength == -1 ? $"{type}(max)" : $"{type}({maxLength})",
        "decimal" or "numeric" => $"{type}({precision},{scale})",
        _ => type,
    };
}
