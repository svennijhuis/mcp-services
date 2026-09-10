using System.Data.Common;
using McpServices.Hosting;
using Microsoft.Extensions.Logging;

namespace McpServices.Storage;

/// <summary>Migration bookkeeping shared by both engines.</summary>
public abstract class KnowledgeStoreBase(ILogger logger) : IKnowledgeStore
{
    private readonly SemaphoreSlim _migrationGate = new(1, 1);

    protected ILogger Logger { get; } = logger;

    public abstract StoreKind Kind { get; }

    public abstract SqlDialect Dialect { get; }

    public abstract string Location { get; }

    public virtual bool NativeVectors => false;

    public int SchemaVersion { get; private set; }

    public abstract Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default);

    public abstract Task<IWriterLock?> TryAcquireWriterLockAsync(string name, CancellationToken cancellationToken = default);

    public abstract Task<string?> DescribeWriterLockAsync(string name, CancellationToken cancellationToken = default);

    public async Task InitializeAsync(IReadOnlyList<Migration> migrations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        await _migrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PrepareAsync(cancellationToken).ConfigureAwait(false);
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync("CREATE TABLE IF NOT EXISTS schema_version (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at BIGINT NOT NULL)", cancellationToken: cancellationToken).ConfigureAwait(false);

            var applied = await connection.ScalarAsync<long?>("SELECT MAX(version) FROM schema_version", cancellationToken: cancellationToken).ConfigureAwait(false) ?? 0;
            var latest = migrations.Count == 0 ? 0 : migrations.Max(m => m.Version);
            if (applied > latest)
            {
                throw new ServerStartupException($"Store at {Location} has schema version {applied}, newer than this build supports ({latest}). Upgrade the server or point it at a different store.");
            }

            foreach (var migration in migrations.Where(m => m.Version > applied).OrderBy(m => m.Version))
            {
                Logger.LogInformation("Applying migration {Version} {Name} to {Location}", migration.Version, migration.Name, Location);
                await ApplyAsync(connection, migration, cancellationToken).ConfigureAwait(false);
            }

            SchemaVersion = (int)Math.Max(applied, latest);
        }
        finally
        {
            _migrationGate.Release();
        }
    }

    /// <summary>Engine-specific one-time setup (create file/directory, extensions, pragmas).</summary>
    protected virtual Task PrepareAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected virtual async Task ApplyAsync(DbConnection connection, Migration migration, CancellationToken cancellationToken)
    {
        var sql = Kind == StoreKind.Sqlite ? migration.SqliteSql : migration.PostgresSql;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(sql))
        {
            await connection.ExecuteAsync(sql, transaction: transaction, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        await connection.ExecuteAsync(
            $"INSERT INTO schema_version (version, name, applied_at) VALUES (@version, @name, {Dialect.NowMs})",
            new { version = migration.Version, name = migration.Name },
            transaction,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public virtual ValueTask DisposeAsync()
    {
        _migrationGate.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
