using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using McpServices.Hosting;

namespace McpServices.Storage;

/// <summary>
/// Tiny ADO.NET convenience layer (a few percent of Dapper) shared by the stores: named
/// <c>@parameters</c> from anonymous objects or dictionaries, scalar/row helpers and JSON columns.
/// </summary>
public static class Db
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();

    public static async Task<int> ExecuteAsync(this DbConnection connection, string sql, object? parameters = null, DbTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        await using var command = Create(connection, sql, parameters, transaction);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T?> ScalarAsync<T>(this DbConnection connection, string sql, object? parameters = null, DbTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        await using var command = Create(connection, sql, parameters, transaction);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert<T>(value);
    }

    public static async Task<List<T>> QueryAsync<T>(this DbConnection connection, string sql, Func<DbDataReader, T> map, object? parameters = null, DbTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        await using var command = Create(connection, sql, parameters, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<T>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(map(reader));
        }

        return results;
    }

    public static async Task<T?> SingleOrDefaultAsync<T>(this DbConnection connection, string sql, Func<DbDataReader, T> map, object? parameters = null, DbTransaction? transaction = null, CancellationToken cancellationToken = default)
        where T : class
    {
        var rows = await QueryAsync(connection, sql, map, parameters, transaction, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public static DbCommand Create(DbConnection connection, string sql, object? parameters, DbTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in Enumerate(parameters))
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@" + name;
            parameter.Value = ToDbValue(value);
            command.Parameters.Add(parameter);
        }

        return command;
    }

    public static IEnumerable<(string Name, object? Value)> Enumerate(object? parameters)
    {
        switch (parameters)
        {
            case null:
                yield break;
            case IReadOnlyDictionary<string, object?> dictionary:
                foreach (var pair in dictionary)
                {
                    yield return (pair.Key, pair.Value);
                }

                yield break;
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                foreach (var pair in pairs)
                {
                    yield return (pair.Key, pair.Value);
                }

                yield break;
        }

        var properties = PropertyCache.GetOrAdd(parameters.GetType(), t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance));
        foreach (var property in properties)
        {
            yield return (property.Name, property.GetValue(parameters));
        }
    }

    private static object ToDbValue(object? value) => value switch
    {
        null => DBNull.Value,
        DateTimeOffset dto => dto.ToUnixTimeMilliseconds(),
        DateTime dt => new DateTimeOffset(dt.ToUniversalTime()).ToUnixTimeMilliseconds(),
        Enum e => e.ToString().ToLowerInvariant(),
        Guid g => g.ToString("N"),
        bool b => b ? 1 : 0,
        string or byte[] or int or long or double or float or decimal or short or byte => value,
        Pgvector.Vector => value,
        float[] floats => VectorCodec.ToBytes(floats),
        IEnumerable<string> strings => JsonSerializer.Serialize(strings, ToolJson.Options),
        _ => JsonSerializer.Serialize(value, ToolJson.Options),
    };

    private static T? Convert<T>(object? value)
    {
        if (value is null || value is DBNull)
        {
            return default;
        }

        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (target.IsInstanceOfType(value))
        {
            return (T)value;
        }

        if (target == typeof(bool))
        {
            return (T)(object)(System.Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0);
        }

        return (T)System.Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }

    // Reader helpers: tolerate engine differences (SQLite INTEGER vs PG bigint/int, 0/1 vs bool).

    public static long GetInt64(this DbDataReader reader, string column) =>
        System.Convert.ToInt64(reader[column], CultureInfo.InvariantCulture);

    public static int GetInt32(this DbDataReader reader, string column) =>
        System.Convert.ToInt32(reader[column], CultureInfo.InvariantCulture);

    public static double GetDouble(this DbDataReader reader, string column) =>
        System.Convert.ToDouble(reader[column], CultureInfo.InvariantCulture);

    public static bool GetBoolean(this DbDataReader reader, string column) =>
        reader[column] switch { bool b => b, var v => System.Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0 };

    public static string GetString(this DbDataReader reader, string column) =>
        reader[column] is DBNull or null ? string.Empty : System.Convert.ToString(reader[column], CultureInfo.InvariantCulture) ?? string.Empty;

    public static string? GetStringOrNull(this DbDataReader reader, string column) =>
        reader[column] is DBNull or null ? null : System.Convert.ToString(reader[column], CultureInfo.InvariantCulture);

    public static long? GetInt64OrNull(this DbDataReader reader, string column) =>
        reader[column] is DBNull or null ? null : System.Convert.ToInt64(reader[column], CultureInfo.InvariantCulture);

    public static DateTimeOffset GetTimestamp(this DbDataReader reader, string column) =>
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(column));

    public static DateTimeOffset? GetTimestampOrNull(this DbDataReader reader, string column) =>
        reader.GetInt64OrNull(column) is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    public static byte[]? GetBytesOrNull(this DbDataReader reader, string column) =>
        reader[column] is byte[] bytes ? bytes : null;

    public static IReadOnlyList<string> GetStringList(this DbDataReader reader, string column)
    {
        var json = reader.GetStringOrNull(column);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, ToolJson.Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static bool HasColumn(this IDataRecord reader, string column)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
