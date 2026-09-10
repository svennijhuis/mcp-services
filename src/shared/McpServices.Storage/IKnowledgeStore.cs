using System.Data.Common;

namespace McpServices.Storage;

/// <summary>
/// A numbered schema step. SQL is written per engine because FTS5 and tsvector differ; both
/// scripts may contain multiple statements. Versions are applied in order and never re-run.
/// </summary>
public sealed record Migration(int Version, string Name, string SqliteSql, string PostgresSql);

/// <summary>
/// One knowledge database (SQLite file or PostgreSQL database). Servers own their table layout
/// through <see cref="Migration"/>s and write engine-neutral SQL where possible; the store handles
/// connections, migrations, single-writer locking and engine capabilities.
/// </summary>
public interface IKnowledgeStore : IAsyncDisposable
{
    StoreKind Kind { get; }

    SqlDialect Dialect { get; }

    /// <summary>Human-readable, secret-free location (file path or redacted connection string).</summary>
    string Location { get; }

    /// <summary>True when vectors can be indexed natively (pgvector). SQLite always scores in-process.</summary>
    bool NativeVectors { get; }

    int SchemaVersion { get; }

    /// <summary>Creates the database if needed and applies pending migrations.</summary>
    Task InitializeAsync(IReadOnlyList<Migration> migrations, CancellationToken cancellationToken = default);

    Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to become the single writer for <paramref name="name"/>. Returns <c>null</c> when
    /// another process holds it; dispose the result to release.
    /// </summary>
    Task<IWriterLock?> TryAcquireWriterLockAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Describes who currently holds the writer lock, or <c>null</c> when free.</summary>
    Task<string?> DescribeWriterLockAsync(string name, CancellationToken cancellationToken = default);
}

public interface IWriterLock : IAsyncDisposable
{
    string Name { get; }
}
