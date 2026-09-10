using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using McpServices.Hosting;

namespace McpServices.Database.Providers;

/// <summary>
/// Shared ADO.NET plumbing: parameter binding (all three engines accept <c>@name</c>), row
/// materialization into JSON-friendly values, timing and truncation.
/// </summary>
public abstract partial class AdoNetProvider(string alias, string connectionString) : IDatabaseProvider
{
    protected const int CommandTimeoutSeconds = 60;

    public string Alias { get; } = alias;

    public abstract string Kind { get; }

    protected string ConnectionString { get; } = connectionString;

    public string RedactedConnectionString => RedactPassword(ConnectionString);

    public abstract DbConnection CreateConnection();

    public abstract Task<IReadOnlyList<TableInfo>> ListTablesAsync(CancellationToken cancellationToken);

    public abstract Task<TableSchema> DescribeTableAsync(string table, string? schema, CancellationToken cancellationToken);

    public abstract Task<string> ExplainAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken);

    /// <summary>Hook for engines that can enforce read-only at the connection or transaction level.</summary>
    protected virtual Task PrepareReadConnectionAsync(DbConnection connection, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Hook to undo connection-level read-only state on pooled connections before a write.</summary>
    protected virtual Task PrepareWriteConnectionAsync(DbConnection connection, CancellationToken cancellationToken) => Task.CompletedTask;

    protected virtual IsolationLevel ReadIsolationLevel => IsolationLevel.ReadCommitted;

    protected virtual async ValueTask<DbTransaction> BeginReadTransactionAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await connection.BeginTransactionAsync(ReadIsolationLevel, cancellationToken).ConfigureAwait(false);

    public async Task PingAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    public virtual async Task<QueryResult> ExecuteReadAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, int maxRows, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await PrepareReadConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var transaction = await BeginReadTransactionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = CommandTimeoutSeconds;
        BindParameters(command, parameters);

        var watch = Stopwatch.StartNew();
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
        var result = await MaterializeAsync(reader, maxRows, cancellationToken).ConfigureAwait(false);
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return result with { ElapsedMs = watch.ElapsedMilliseconds };
    }

    public virtual async Task<WriteResult> ExecuteWriteAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await PrepareWriteConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = CommandTimeoutSeconds;
        BindParameters(command, parameters);

        var watch = Stopwatch.StartNew();
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return new WriteResult(affected, watch.ElapsedMilliseconds);
    }

    protected async Task<QueryResult> QueryAsync(DbConnection connection, string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = CommandTimeoutSeconds;
        BindParameters(command, parameters);
        var watch = Stopwatch.StartNew();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = await MaterializeAsync(reader, int.MaxValue, cancellationToken).ConfigureAwait(false);
        return result with { ElapsedMs = watch.ElapsedMilliseconds };
    }

    protected static async Task<QueryResult> MaterializeAsync(DbDataReader reader, int maxRows, CancellationToken cancellationToken)
    {
        var columns = new List<string>(reader.FieldCount);
        var types = new List<string>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            columns.Add(reader.GetName(i));
            types.Add(SafeTypeName(reader, i));
        }

        var rows = new List<object?[]>();
        var truncated = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (rows.Count >= maxRows)
            {
                truncated = true;
                break;
            }

            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[i] = await reader.IsDBNullAsync(i, cancellationToken).ConfigureAwait(false) ? null : ToJsonFriendly(reader.GetValue(i));
            }

            rows.Add(row);
        }

        return new QueryResult(columns, types, rows, truncated, 0);
    }

    private static string SafeTypeName(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetDataTypeName(ordinal);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IndexOutOfRangeException or NotSupportedException)
        {
            return reader.GetFieldType(ordinal).Name;
        }
    }

    protected static object? ToJsonFriendly(object value) => value switch
    {
        DBNull => null,
        byte[] bytes => bytes.Length > 64 * 1024 ? $"<{bytes.Length} bytes>" : Convert.ToBase64String(bytes),
        DateTime dt => dt.Kind == DateTimeKind.Unspecified ? dt.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF", System.Globalization.CultureInfo.InvariantCulture) : dt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.FFFFFFF", System.Globalization.CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString(),
        Guid g => g.ToString(),
        decimal or double or float or int or long or short or byte or bool or string => value,
        Array array => array.Cast<object?>().Select(v => v is null ? null : ToJsonFriendly(v)).ToArray(),
        JsonElement je => je,
        _ => value.ToString(),
    };

    protected static void BindParameters(DbCommand command, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (parameters is null)
        {
            return;
        }

        foreach (var (name, raw) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name.StartsWith('@') ? name : "@" + name;
            parameter.Value = NormalizeParameterValue(raw) ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }

    private static object? NormalizeParameterValue(object? raw) => raw switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.Null } => null,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        JsonElement { ValueKind: JsonValueKind.Number } je => je.TryGetInt64(out var l) ? l : je.GetDouble(),
        JsonElement je => je.GetRawText(),
        _ => raw,
    };

    public static string RedactPassword(string connectionString) =>
        PasswordRegex().Replace(connectionString, "$1=***");

    [GeneratedRegex(@"(?i)\b(password|pwd|pass)\s*=\s*[^;]*", RegexOptions.CultureInvariant)]
    private static partial Regex PasswordRegex();

    protected static void EnsureNoParameters(IReadOnlyDictionary<string, object?>? parameters, string feature)
    {
        if (parameters is { Count: > 0 })
        {
            throw new ToolException($"{feature} does not support parameters on this database engine; inline literal values instead.");
        }
    }
}
