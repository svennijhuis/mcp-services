using McpServices.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace McpServices.Storage;

public static class KnowledgeStoreFactory
{
    public static IKnowledgeStore Create(StoreOptions options, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        return options.Kind switch
        {
            StoreKind.Sqlite => new SqliteStore(options, loggerFactory.CreateLogger<SqliteStore>()),
            StoreKind.Postgres => new PostgresStore(options, loggerFactory.CreateLogger<PostgresStore>()),
            _ => throw new ServerStartupException($"Unsupported store kind {options.Kind}."),
        };
    }

    /// <summary>
    /// Resolves the store for a server: <c>--store</c>, then <c>MCP_&lt;SERVER&gt;_STORE</c>, then the
    /// SQLite default under <see cref="StoreOptions.DataHome"/>.
    /// </summary>
    public static StoreOptions Resolve(HostContext context, string serverName, string defaultFileName)
    {
        ArgumentNullException.ThrowIfNull(context);
        var envName = "MCP_" + serverName.Replace("mcp-", string.Empty, StringComparison.OrdinalIgnoreCase).Replace('-', '_').ToUpperInvariant() + "_STORE";
        var definition = context.Args.GetOption("store", envName);
        var options = string.IsNullOrWhiteSpace(definition)
            ? StoreOptions.DefaultSqlite(serverName, defaultFileName)
            : StoreOptions.Parse(definition);

        context.Expose("store", new { kind = options.Kind.ToString().ToLowerInvariant(), location = options.Redacted });
        return options;
    }

    /// <summary>Registers the store as a singleton that is initialised (migrated) before first use.</summary>
    public static void AddKnowledgeStore(this HostContext context, StoreOptions options, IReadOnlyList<Migration> migrations)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Services.AddSingleton(options);
        context.Services.AddSingleton<IKnowledgeStore>(sp => Create(options, sp.GetRequiredService<ILoggerFactory>()));
        context.Services.AddSingleton(new MigrationSet(migrations));
        context.Services.AddSingleton<StoreInitializer>();
    }
}

public sealed record MigrationSet(IReadOnlyList<Migration> Migrations);

/// <summary>
/// Initialises the store once on first use, so tools can <c>await initializer.StoreAsync()</c>. A
/// failed attempt (database down) is retried on the next call instead of being cached forever.
/// </summary>
public sealed class StoreInitializer(IKnowledgeStore store, MigrationSet migrations) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _initialized;

    public void Dispose() => _gate.Dispose();

    public async Task<IKnowledgeStore> StoreAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return store;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_initialized)
            {
                await store.InitializeAsync(migrations.Migrations, cancellationToken).ConfigureAwait(false);
                _initialized = true;
            }
        }
        finally
        {
            _gate.Release();
        }

        return store;
    }
}
