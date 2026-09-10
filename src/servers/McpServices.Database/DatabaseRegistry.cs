using System.Text.RegularExpressions;
using McpServices.Database.Providers;
using McpServices.Hosting;

namespace McpServices.Database;

/// <summary>
/// Databases are registered once at startup under an alias (<c>--db main=sqlite:./app.db</c> or
/// <c>MCP_DB__main=postgres:Host=...</c>). Tools select a database by alias and can never open
/// arbitrary files or connection strings at runtime.
/// </summary>
public sealed partial class DatabaseRegistry
{
    private readonly Dictionary<string, IDatabaseProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public DatabaseRegistry(IEnumerable<string> definitions, bool readOnly)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ReadOnly = readOnly;
        foreach (var definition in definitions)
        {
            var provider = Parse(definition);
            if (!_providers.TryAdd(provider.Alias, provider))
            {
                throw new ServerStartupException($"Database alias '{provider.Alias}' is registered twice.");
            }
        }

        if (_providers.Count == 0)
        {
            throw new ServerStartupException("Register at least one database with --db <alias>=<provider>:<connection> or MCP_DB__<alias>=<provider>:<connection>. Providers: sqlite, postgres, sqlserver.");
        }
    }

    public bool ReadOnly { get; }

    public IReadOnlyCollection<IDatabaseProvider> All => _providers.Values;

    public IDatabaseProvider Get(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            if (_providers.Count == 1)
            {
                return _providers.Values.First();
            }

            throw new ToolException($"Parameter 'database' is required because {_providers.Count} databases are registered: {string.Join(", ", _providers.Keys)}.");
        }

        return _providers.TryGetValue(alias, out var provider)
            ? provider
            : throw new ToolException($"Unknown database '{alias}'. Registered: {string.Join(", ", _providers.Keys)}.");
    }

    public static IEnumerable<string> FromEnvironment()
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key.ToString() ?? string.Empty;
            if (key.StartsWith("MCP_DB__", StringComparison.OrdinalIgnoreCase) && entry.Value is string value && !string.IsNullOrWhiteSpace(value))
            {
                yield return $"{key["MCP_DB__".Length..]}={value}";
            }
        }
    }

    public static IDatabaseProvider Parse(string definition)
    {
        var match = DefinitionRegex().Match(definition ?? string.Empty);
        if (!match.Success)
        {
            throw new ServerStartupException($"Invalid database definition '{definition}'. Expected <alias>=<provider>:<connection-string>, e.g. main=sqlite:./data/app.db");
        }

        var alias = match.Groups["alias"].Value;
        var kind = match.Groups["kind"].Value.ToLowerInvariant();
        var connection = match.Groups["conn"].Value.Trim();
        return kind switch
        {
            "sqlite" or "sqlite3" => new SqliteProvider(alias, connection),
            "postgres" or "postgresql" or "pg" or "npgsql" => new PostgresProvider(alias, connection),
            "sqlserver" or "mssql" or "sqlclient" => new SqlServerProvider(alias, connection),
            _ => throw new ServerStartupException($"Unknown database provider '{kind}' for alias '{alias}'. Use sqlite, postgres or sqlserver."),
        };
    }

    [GeneratedRegex(@"^(?<alias>[A-Za-z0-9_-]+)=(?<kind>[A-Za-z0-9]+):(?<conn>.+)$", RegexOptions.Singleline)]
    private static partial Regex DefinitionRegex();
}
