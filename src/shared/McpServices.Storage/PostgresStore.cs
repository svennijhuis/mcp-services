using System.Data.Common;
using McpServices.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector.Npgsql;

namespace McpServices.Storage;

/// <summary>
/// Shared store for teams, Docker and remote agents: PostgreSQL with pgvector when available.
/// Single-writer coordination uses session advisory locks held on a dedicated connection.
/// </summary>
public sealed class PostgresStore : KnowledgeStoreBase
{
    private readonly StoreOptions _options;
    private readonly NpgsqlDataSource _dataSource;
    private bool _nativeVectors;

    public PostgresStore(StoreOptions options, ILogger<PostgresStore> logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Kind != StoreKind.Postgres)
        {
            throw new ArgumentException("PostgresStore requires postgres options.", nameof(options));
        }

        _options = options;
        try
        {
            var builder = new NpgsqlDataSourceBuilder(options.ConnectionString);
            builder.UseVector();
            _dataSource = builder.Build();
        }
        catch (ArgumentException ex)
        {
            throw new ServerStartupException($"Invalid PostgreSQL connection string: {ex.Message}", ex);
        }
    }

    public override StoreKind Kind => StoreKind.Postgres;

    public override SqlDialect Dialect => SqlDialect.Postgres;

    public override string Location => _options.Redacted;

    public override bool NativeVectors => _nativeVectors;

    protected override async Task PrepareAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await connection.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS vector", cancellationToken: cancellationToken).ConfigureAwait(false);
                await connection.ReloadTypesAsync(cancellationToken).ConfigureAwait(false);
                _nativeVectors = true;
            }
            catch (PostgresException ex)
            {
                Logger.LogWarning("pgvector is not available ({Code}: {Message}); embeddings will be scored in-process.", ex.SqlState, ex.MessageText);
                _nativeVectors = false;
            }
        }
        catch (NpgsqlException ex) when (ex is not PostgresException)
        {
            throw new ServerStartupException($"Cannot reach PostgreSQL at {Location}: {ex.Message}", ex);
        }
    }

    public override async Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default) =>
        await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

    public override async Task<IWriterLock?> TryAcquireWriterLockAsync(string name, CancellationToken cancellationToken = default)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var acquired = await connection.ScalarAsync<bool>("SELECT pg_try_advisory_lock(hashtext(@name))", new { name }, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!acquired)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            await connection.ExecuteAsync(
                "CREATE TABLE IF NOT EXISTS writer_locks (name TEXT PRIMARY KEY, holder TEXT NOT NULL, acquired_at BIGINT NOT NULL); " +
                $"INSERT INTO writer_locks (name, holder, acquired_at) VALUES (@name, @holder, {Dialect.NowMs}) ON CONFLICT (name) DO UPDATE SET holder = excluded.holder, acquired_at = excluded.acquired_at",
                new { name, holder = $"pid {Environment.ProcessId} on {Environment.MachineName}" },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return new AdvisoryLock(name, connection);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public override async Task<string?> DescribeWriterLockAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var free = await connection.ScalarAsync<bool>("SELECT pg_try_advisory_lock(hashtext(@name))", new { name }, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (free)
        {
            await connection.ExecuteAsync("SELECT pg_advisory_unlock(hashtext(@name))", new { name }, cancellationToken: cancellationToken).ConfigureAwait(false);
            return null;
        }

        var exists = await connection.ScalarAsync<bool>("SELECT to_regclass('writer_locks') IS NOT NULL", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!exists)
        {
            return "another session";
        }

        var holder = await connection.ScalarAsync<string?>("SELECT holder FROM writer_locks WHERE name = @name", new { name }, cancellationToken: cancellationToken).ConfigureAwait(false);
        return holder ?? "another session";
    }

    public override async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class AdvisoryLock(string name, NpgsqlConnection connection) : IWriterLock
    {
        public string Name { get; } = name;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await connection.ExecuteAsync("SELECT pg_advisory_unlock(hashtext(@name))", new { name = Name }).ConfigureAwait(false);
                await connection.ExecuteAsync("DELETE FROM writer_locks WHERE name = @name", new { name = Name }).ConfigureAwait(false);
            }
            catch (NpgsqlException)
            {
                // Closing the session releases the advisory lock anyway.
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
