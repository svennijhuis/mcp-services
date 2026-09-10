using System.Data.Common;

namespace McpServices.Database.Providers;

public sealed record TableInfo(string? Schema, string Name, string Type, long? EstimatedRows);

public sealed record ColumnInfo(string Name, string DataType, bool Nullable, string? DefaultValue, bool PrimaryKey, int Ordinal);

public sealed record ForeignKeyInfo(string Name, string Column, string ReferencedTable, string ReferencedColumn);

public sealed record IndexInfo(string Name, bool Unique, IReadOnlyList<string> Columns);

public sealed record TableSchema(string? Schema, string Name, IReadOnlyList<ColumnInfo> Columns, IReadOnlyList<ForeignKeyInfo> ForeignKeys, IReadOnlyList<IndexInfo> Indexes);

public sealed record QueryResult(IReadOnlyList<string> Columns, IReadOnlyList<string> ColumnTypes, IReadOnlyList<object?[]> Rows, bool Truncated, long ElapsedMs);

public sealed record WriteResult(int AffectedRows, long ElapsedMs);

/// <summary>One registered database. Implementations differ only in schema queries and engine quirks.</summary>
public interface IDatabaseProvider
{
    string Alias { get; }

    /// <summary>sqlite | postgres | sqlserver</summary>
    string Kind { get; }

    /// <summary>Connection string with any password removed, safe to show to agents.</summary>
    string RedactedConnectionString { get; }

    DbConnection CreateConnection();

    Task<IReadOnlyList<TableInfo>> ListTablesAsync(CancellationToken cancellationToken);

    Task<TableSchema> DescribeTableAsync(string table, string? schema, CancellationToken cancellationToken);

    Task<QueryResult> ExecuteReadAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, int maxRows, CancellationToken cancellationToken);

    Task<WriteResult> ExecuteWriteAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken);

    Task<string> ExplainAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken);

    Task PingAsync(CancellationToken cancellationToken);
}
