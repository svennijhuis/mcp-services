using McpServices.Hosting;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace McpServices.Storage;

public enum StoreKind
{
    Sqlite,
    Postgres,
}

/// <summary>
/// Where a server keeps its knowledge. Parsed from <c>--store sqlite:&lt;path&gt;</c>,
/// <c>--store postgres:&lt;connection-string&gt;</c> or a bare file path (SQLite).
/// </summary>
public sealed record StoreOptions(StoreKind Kind, string ConnectionString)
{
    /// <summary>Root for per-server SQLite files: <c>~/.mcp-services</c> or <c>MCP_SERVICES_HOME</c>.</summary>
    public static string DataHome =>
        Environment.GetEnvironmentVariable("MCP_SERVICES_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mcp-services");

    /// <summary>Connection string with passwords removed, safe to show to agents.</summary>
    public string Redacted => Kind switch
    {
        StoreKind.Sqlite => new SqliteConnectionStringBuilder(ConnectionString).DataSource,
        _ => RedactPostgres(ConnectionString),
    };

    public static StoreOptions Parse(string definition)
    {
        ToolGuard.NotEmpty(definition, "store");
        var colon = definition.IndexOf(':', StringComparison.Ordinal);
        var scheme = colon > 0 ? definition[..colon].ToLowerInvariant() : string.Empty;
        var rest = colon > 0 ? definition[(colon + 1)..] : definition;

        return scheme switch
        {
            "sqlite" or "sqlite3" => Sqlite(rest),
            "postgres" or "postgresql" or "pg" or "npgsql" => new StoreOptions(StoreKind.Postgres, rest),
            "" => Sqlite(definition),
            _ when scheme.Length == 1 => Sqlite(definition), // Windows drive letter, e.g. C:\data\index.db
            _ => throw new ServerStartupException($"Unknown store '{scheme}'. Use sqlite:<path> or postgres:<connection-string>."),
        };
    }

    /// <summary>SQLite file inside <see cref="DataHome"/>/<paramref name="serverName"/>.</summary>
    public static StoreOptions DefaultSqlite(string serverName, string fileName) =>
        Sqlite(Path.Combine(DataHome, serverName, fileName));

    public static StoreOptions Sqlite(string pathOrConnectionString)
    {
        ToolGuard.NotEmpty(pathOrConnectionString, "store");
        if (pathOrConnectionString.Contains('=', StringComparison.Ordinal))
        {
            return new StoreOptions(StoreKind.Sqlite, pathOrConnectionString);
        }

        var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(pathOrConnectionString));
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        };
        return new StoreOptions(StoreKind.Sqlite, builder.ToString());
    }

    private static string RedactPostgres(string connectionString)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (!string.IsNullOrEmpty(builder.Password))
            {
                builder.Password = "***";
            }

            return builder.ToString();
        }
        catch (ArgumentException)
        {
            return "postgres:<invalid connection string>";
        }
    }
}
