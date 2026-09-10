using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using McpServices.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace McpServices.Storage;

/// <summary>
/// Single-machine store: one SQLite file in WAL mode. Readers never block; the single-writer
/// guarantee across processes (Cursor and Claude both running a server) is a lock file with PID
/// and heartbeat next to the database.
/// </summary>
public sealed class SqliteStore : KnowledgeStoreBase
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(90);

    private readonly string _connectionString;
    private readonly string? _path;

    public SqliteStore(StoreOptions options, ILogger<SqliteStore> logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Kind != StoreKind.Sqlite)
        {
            throw new ArgumentException("SqliteStore requires sqlite options.", nameof(options));
        }

        _connectionString = options.ConnectionString;
        var dataSource = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        _path = dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase) ? null : Path.GetFullPath(dataSource);
    }

    public override StoreKind Kind => StoreKind.Sqlite;

    public override SqlDialect Dialect => SqlDialect.Sqlite;

    public override string Location => _path ?? "sqlite::memory:";

    /// <summary>Database file, or <c>null</c> for in-memory stores.</summary>
    public string? FilePath => _path;

    protected override Task PrepareAsync(CancellationToken cancellationToken)
    {
        if (_path is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        }

        return Task.CompletedTask;
    }

    public override async Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON; PRAGMA temp_store = MEMORY;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public override Task<IWriterLock?> TryAcquireWriterLockAsync(string name, CancellationToken cancellationToken = default)
    {
        if (_path is null)
        {
            return Task.FromResult<IWriterLock?>(new NoopLock(name));
        }

        var lockPath = LockPath(name);
        if (TryReadLock(lockPath) is { } existing && !IsStale(existing))
        {
            return Task.FromResult<IWriterLock?>(null);
        }

        try
        {
            File.Delete(lockPath);
            using var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, LockInfo.ForCurrentProcess(), ToolJson.Options);
        }
        catch (IOException)
        {
            return Task.FromResult<IWriterLock?>(null);
        }

        return Task.FromResult<IWriterLock?>(new FileLock(name, lockPath));
    }

    public override Task<string?> DescribeWriterLockAsync(string name, CancellationToken cancellationToken = default)
    {
        if (_path is null)
        {
            return Task.FromResult<string?>(null);
        }

        var info = TryReadLock(LockPath(name));
        return Task.FromResult(info is null || IsStale(info) ? null : $"pid {info.Pid} on {info.Host} since {info.StartedAt:u}");
    }

    private string LockPath(string name) => _path + "." + Sanitize(name) + ".lock";

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    private static LockInfo? TryReadLock(string lockPath)
    {
        try
        {
            if (!File.Exists(lockPath))
            {
                return null;
            }

            using var stream = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<LockInfo>(stream, ToolJson.Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsStale(LockInfo info)
    {
        if (DateTimeOffset.UtcNow - info.Heartbeat > StaleAfter)
        {
            return true;
        }

        if (!string.Equals(info.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(info.Pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private sealed record LockInfo(int Pid, string Host, DateTimeOffset StartedAt, DateTimeOffset Heartbeat)
    {
        public static LockInfo ForCurrentProcess()
        {
            var now = DateTimeOffset.UtcNow;
            return new LockInfo(Environment.ProcessId, Environment.MachineName, now, now);
        }
    }

    private sealed class FileLock : IWriterLock
    {
        private readonly string _lockPath;
        private readonly Timer _heartbeat;
        private readonly LockInfo _info = LockInfo.ForCurrentProcess();

        public FileLock(string name, string lockPath)
        {
            Name = name;
            _lockPath = lockPath;
            _heartbeat = new Timer(_ => Beat(), null, HeartbeatInterval, HeartbeatInterval);
        }

        public string Name { get; }

        private void Beat()
        {
            try
            {
                var tmp = _lockPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_info with { Heartbeat = DateTimeOffset.UtcNow }, ToolJson.Options));
                File.Move(tmp, _lockPath, overwrite: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _heartbeat.DisposeAsync().ConfigureAwait(false);
            try
            {
                File.Delete(_lockPath);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class NoopLock(string name) : IWriterLock
    {
        public string Name { get; } = name;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
