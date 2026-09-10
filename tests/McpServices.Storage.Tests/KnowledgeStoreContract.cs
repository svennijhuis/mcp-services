using McpServices.Hosting;
using McpServices.Storage;
using McpServices.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpServices.Storage.Tests;

/// <summary>Behaviour every store must share; run against SQLite always and PostgreSQL when configured.</summary>
public abstract class KnowledgeStoreContract
{
    protected static readonly IReadOnlyList<Migration> Migrations =
    [
        new(1, "notes",
            SqliteSql: """
                CREATE TABLE notes (id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL, body TEXT NOT NULL, tags TEXT NOT NULL DEFAULT '[]', created_at BIGINT NOT NULL);
                CREATE VIRTUAL TABLE notes_fts USING fts5(title, body, content='notes', content_rowid='id');
                CREATE TRIGGER notes_ai AFTER INSERT ON notes BEGIN INSERT INTO notes_fts(rowid, title, body) VALUES (new.id, new.title, new.body); END;
                CREATE TRIGGER notes_ad AFTER DELETE ON notes BEGIN INSERT INTO notes_fts(notes_fts, rowid, title, body) VALUES ('delete', old.id, old.title, old.body); END;
                """,
            PostgresSql: """
                CREATE TABLE notes (id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY, title TEXT NOT NULL, body TEXT NOT NULL, tags TEXT NOT NULL DEFAULT '[]', created_at BIGINT NOT NULL,
                    tsv tsvector GENERATED ALWAYS AS (to_tsvector('simple', title || ' ' || body)) STORED);
                CREATE INDEX notes_tsv ON notes USING GIN (tsv);
                """),
        new(2, "notes_pinned",
            SqliteSql: "ALTER TABLE notes ADD COLUMN pinned INTEGER NOT NULL DEFAULT 0",
            PostgresSql: "ALTER TABLE notes ADD COLUMN pinned INTEGER NOT NULL DEFAULT 0"),
    ];

    protected abstract Task<IKnowledgeStore> CreateStoreAsync();

    protected async Task Applies_migrations_once_and_reports_version_impl()
    {
        await using var store = await CreateStoreAsync();
        await store.InitializeAsync(Migrations);
        Assert.Equal(2, store.SchemaVersion);

        await store.InitializeAsync(Migrations);
        await using var connection = await store.OpenAsync();
        var applied = await connection.QueryAsync("SELECT version, name FROM schema_version ORDER BY version", r => (r.GetInt32("version"), r.GetString("name")));
        Assert.Equal([(1, "notes"), (2, "notes_pinned")], applied);

        var newer = Migrations.Take(1).ToList();
        var ex = await Assert.ThrowsAsync<ServerStartupException>(() => store.InitializeAsync(newer));
        Assert.Contains("newer", ex.Message, StringComparison.Ordinal);
    }

    protected async Task Writes_reads_and_full_text_searches_impl()
    {
        await using var store = await CreateStoreAsync();
        await store.InitializeAsync(Migrations);
        await using var connection = await store.OpenAsync();

        var now = DateTimeOffset.UtcNow;
        string[] tags = ["ordering", "service"];
        await connection.ExecuteAsync(
            $"INSERT INTO notes (title, body, tags, created_at) VALUES (@title, @body, @tags, @created)",
            new { title = "OrderService submit", body = "Ordering goes through OrderService.Submit, not the controller", tags, created = now });
        await connection.ExecuteAsync(
            $"INSERT INTO notes (title, body, tags, created_at) VALUES (@title, @body, @tags, {store.Dialect.NowMs})",
            new { title = "Unrelated", body = "Build takes forty seconds", tags = Array.Empty<string>() });

        var count = await connection.ScalarAsync<long>("SELECT COUNT(*) FROM notes");
        Assert.Equal(2, count);

        var note = await connection.SingleOrDefaultAsync(
            "SELECT id, title, tags, created_at, pinned FROM notes WHERE title = @title",
            r => new { Id = r.GetInt64("id"), Tags = r.GetStringList("tags"), Created = r.GetTimestamp("created_at"), Pinned = r.GetBoolean("pinned") },
            new { title = "OrderService submit" });
        Assert.NotNull(note);
        Assert.Equal(tags, note.Tags);
        Assert.Equal(now.ToUnixTimeMilliseconds(), note.Created.ToUnixTimeMilliseconds());
        Assert.False(note.Pinned);

        var hits = await SearchAsync(store, connection, "orderservice submit");
        Assert.Equal(["OrderService submit"], hits);

        var prefix = await SearchAsync(store, connection, "contr");
        Assert.Equal(["OrderService submit"], prefix);

        var none = await SearchAsync(store, connection, "nonexistent");
        Assert.Empty(none);
    }

    protected async Task Writer_lock_is_exclusive_and_released_impl()
    {
        await using var store = await CreateStoreAsync();
        await store.InitializeAsync(Migrations);
        var name = "indexer-" + Guid.NewGuid().ToString("N")[..8];

        Assert.Null(await store.DescribeWriterLockAsync(name));
        var first = await store.TryAcquireWriterLockAsync(name);
        Assert.NotNull(first);
        Assert.Null(await store.TryAcquireWriterLockAsync(name));
        Assert.Contains(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), await store.DescribeWriterLockAsync(name) ?? string.Empty, StringComparison.Ordinal);

        await first.DisposeAsync();
        Assert.Null(await store.DescribeWriterLockAsync(name));
        var second = await store.TryAcquireWriterLockAsync(name);
        Assert.NotNull(second);
        await second.DisposeAsync();
    }

    private static async Task<List<string>> SearchAsync(IKnowledgeStore store, System.Data.Common.DbConnection connection, string query)
    {
        var q = store.Dialect.FullTextQuery(query);
        return store.Kind == StoreKind.Sqlite
            ? await connection.QueryAsync("SELECT n.title FROM notes_fts f JOIN notes n ON n.id = f.rowid WHERE notes_fts MATCH @q ORDER BY bm25(notes_fts)", r => r.GetString("title"), new { q })
            : await connection.QueryAsync("SELECT title FROM notes WHERE tsv @@ to_tsquery('simple', @q) ORDER BY ts_rank_cd(tsv, to_tsquery('simple', @q)) DESC", r => r.GetString("title"), new { q });
    }
}

public class SqliteStoreTests : KnowledgeStoreContract, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mcp-storage-tests-" + Guid.NewGuid().ToString("N"));

    protected override Task<IKnowledgeStore> CreateStoreAsync()
    {
        var options = StoreOptions.Sqlite(Path.Combine(_directory, "nested", "store.db"));
        return Task.FromResult<IKnowledgeStore>(new SqliteStore(options, NullLogger<SqliteStore>.Instance));
    }

    [Fact]
    public Task Applies_migrations_once_and_reports_version() => Applies_migrations_once_and_reports_version_impl();

    [Fact]
    public Task Writes_reads_and_full_text_searches() => Writes_reads_and_full_text_searches_impl();

    [Fact]
    public Task Writer_lock_is_exclusive_and_released() => Writer_lock_is_exclusive_and_released_impl();

    [Fact]
    public async Task Creates_directories_and_uses_wal()
    {
        await using var store = (SqliteStore)await CreateStoreAsync();
        await store.InitializeAsync(Migrations);
        Assert.True(File.Exists(store.FilePath));
        await using var connection = await store.OpenAsync();
        Assert.Equal("wal", await connection.ScalarAsync<string>("PRAGMA journal_mode"));
    }

    [Fact]
    public async Task Stale_lock_files_are_taken_over()
    {
        await using var store = (SqliteStore)await CreateStoreAsync();
        await store.InitializeAsync(Migrations);
        var lockPath = store.FilePath + ".indexer.lock";
        var old = DateTimeOffset.UtcNow.AddMinutes(-10);
        await File.WriteAllTextAsync(lockPath, ToolJson.Serialize(new { pid = 999999, host = Environment.MachineName, startedAt = old, heartbeat = old }));

        Assert.Null(await store.DescribeWriterLockAsync("indexer"));
        var acquired = await store.TryAcquireWriterLockAsync("indexer");
        Assert.NotNull(acquired);
        await acquired.DisposeAsync();
        Assert.False(File.Exists(lockPath));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>Run with <c>MCP_TEST_POSTGRES="Host=localhost;Username=postgres;Password=postgres;Database=postgres"</c>.</summary>
public class PostgresStoreTests : KnowledgeStoreContract
{
    private const string Variable = "MCP_TEST_POSTGRES";

    protected override async Task<IKnowledgeStore> CreateStoreAsync()
    {
        var schema = "mcp_store_" + Guid.NewGuid().ToString("N")[..8];
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Variable)!) { SearchPath = schema };
        await using (var admin = new Npgsql.NpgsqlConnection(builder.ToString()))
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync($"CREATE SCHEMA {schema}");
        }

        return new SchemaScopedStore(new PostgresStore(new StoreOptions(StoreKind.Postgres, builder.ToString()), NullLogger<PostgresStore>.Instance), builder.ToString(), schema);
    }

    [EnvironmentFact(Variable)]
    public Task Applies_migrations_once_and_reports_version() => Applies_migrations_once_and_reports_version_impl();

    [EnvironmentFact(Variable)]
    public Task Writes_reads_and_full_text_searches() => Writes_reads_and_full_text_searches_impl();

    [EnvironmentFact(Variable)]
    public Task Writer_lock_is_exclusive_and_released() => Writer_lock_is_exclusive_and_released_impl();

    /// <summary>Drops the throw-away schema when the test is done with the store.</summary>
    private sealed class SchemaScopedStore(PostgresStore inner, string connectionString, string schema) : IKnowledgeStore
    {
        public StoreKind Kind => inner.Kind;

        public SqlDialect Dialect => inner.Dialect;

        public string Location => inner.Location;

        public bool NativeVectors => inner.NativeVectors;

        public int SchemaVersion => inner.SchemaVersion;

        public Task InitializeAsync(IReadOnlyList<Migration> migrations, CancellationToken cancellationToken = default) => inner.InitializeAsync(migrations, cancellationToken);

        public Task<System.Data.Common.DbConnection> OpenAsync(CancellationToken cancellationToken = default) => inner.OpenAsync(cancellationToken);

        public Task<IWriterLock?> TryAcquireWriterLockAsync(string name, CancellationToken cancellationToken = default) => inner.TryAcquireWriterLockAsync(name, cancellationToken);

        public Task<string?> DescribeWriterLockAsync(string name, CancellationToken cancellationToken = default) => inner.DescribeWriterLockAsync(name, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await using var admin = new Npgsql.NpgsqlConnection(connectionString);
            await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {schema} CASCADE");
        }
    }
}
