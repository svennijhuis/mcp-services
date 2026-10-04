using System.Data.Common;
using System.Diagnostics;
using McpServices.Index.Commit;
using McpServices.Index.Indexing;
using McpServices.Storage;
using McpServices.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace McpServices.Index.Tests;

public class CommitKnowledgeTests
{
    private const string PostgresVariable = "MCP_TEST_POSTGRES";

    private const string Source = """
        namespace Demo;

        /// <summary>Doc that must not become a why.</summary>
        public interface IPipe
        {
            void Flush();
        }

        public sealed class Pipe : IPipe
        {
            public void Flush()
            {
            }
        }

        public sealed class App
        {
            public void Main()
            {
                // version A
                new Pipe().Flush();
            }
        }
        """;

    [Fact]
    public async Task Scip_json_and_protobuf_supply_symbols_when_present()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcp-index-scip-" + Guid.NewGuid().ToString("N") + ".db");
        var root = Path.Combine(Path.GetTempPath(), "mcp-index-scip-repo-" + Guid.NewGuid().ToString("N"));
        var store = new SqliteStore(StoreOptions.Sqlite(path), NullLogger<SqliteStore>.Instance);
        try
        {
            await store.InitializeAsync(IndexSchema.Migrations);
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "Pipe.cs"), Source);
            await Git(root, "init", "-q");
            await Git(root, "add", "-A");
            await Git(root, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "-m", "a");
            const string symbol = "scip-test . . Demo/App#Main().";
            await File.WriteAllTextAsync(Path.Combine(root, "index.scip.json"), $$"""
                {
                  "documents": [{
                    "relative_path": "Pipe.cs",
                    "symbols": [{
                      "symbol": "{{symbol}}",
                      "kind": "Method",
                      "display_name": "Main",
                      "documentation": ["From the indexer."],
                      "relationships": [{ "symbol": "scip-test . . Demo/IPipe#", "is_implementation": true }]
                    }],
                    "occurrences": [{ "range": [20, 16, 20, 20], "symbol": "{{symbol}}", "symbol_roles": 1 }]
                  }],
                  "external_symbols": [{ "symbol": "scip-test . . Demo/IPipe#", "kind": "Interface", "display_name": "IPipe" }]
                }
                """);

            var knowledge = new CommitKnowledge(store, new IndexRepository(store), new IndexOptions(), NullLogger<CommitKnowledge>.Instance);
            var identity = RepoIdentity.FromPath(root);
            var json = await knowledge.ReindexAsync(identity, null, CancellationToken.None);
            Assert.Equal("scip", json.Source);
            Assert.True(json.Files >= 1);
            var why = await knowledge.WhyAsync(identity, symbol, null, false, null, CancellationToken.None);
            Assert.True(why.Unknown);
            Assert.Equal(symbol, why.Anchor);
            Assert.DoesNotContain("From the indexer", string.Join('\n', why.Rationale.Select(r => r.Text)), StringComparison.Ordinal);
            await using var connection = await store.OpenAsync();
            Assert.Equal(1, await connection.ScalarAsync<long>("SELECT COUNT(*) FROM symbol_edge WHERE repo_id = @repoId AND kind = 'implements'", new { repoId = identity.RepoId }));
            Assert.Equal("From the indexer.", await connection.ScalarAsync<string>("SELECT doc FROM symbol WHERE symbol_key = @symbol", new { symbol }));

            File.Delete(Path.Combine(root, "index.scip.json"));
            await File.WriteAllBytesAsync(Path.Combine(root, "index.scip"), ScipBytes("Pipe.cs", "scip-bin . . Demo/App#Main().", "Main"));
            var binary = await knowledge.ReindexAsync(identity, null, CancellationToken.None);
            Assert.Equal("scip", binary.Source);
            Assert.Equal(1, await connection.ScalarAsync<long>("SELECT COUNT(*) FROM symbol WHERE name = 'Main' AND scip_symbol = 'scip-bin . . Demo/App#Main().'"));
        }
        finally
        {
            await store.DisposeAsync();
            SqliteConnection.ClearAllPools();
            DeleteTree(root);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static byte[] ScipBytes(string path, string symbol, string display)
    {
        var index = new List<byte>();
        var document = new List<byte>();
        ProtoString(document, 1, path);
        var info = new List<byte>();
        ProtoString(info, 1, symbol);
        ProtoVarintField(info, 5, 26);
        ProtoString(info, 6, display);
        ProtoBytes(document, 3, info.ToArray());
        var occurrence = new List<byte>();
        var range = new List<byte>();
        foreach (var part in new ulong[] { 20, 16, 20, 20 })
        {
            ProtoVarint(range, part);
        }

        ProtoBytes(occurrence, 1, range.ToArray());
        ProtoString(occurrence, 2, symbol);
        ProtoVarintField(occurrence, 3, 1);
        ProtoBytes(document, 2, occurrence.ToArray());
        ProtoBytes(index, 2, document.ToArray());
        return index.ToArray();
    }

    private static void ProtoVarint(List<byte> bytes, ulong value)
    {
        do
        {
            var current = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                current |= 0x80;
            }

            bytes.Add(current);
        }
        while (value != 0);
    }

    private static void ProtoVarintField(List<byte> bytes, int field, ulong value)
    {
        ProtoVarint(bytes, (ulong)((field << 3) | 0));
        ProtoVarint(bytes, value);
    }

    private static void ProtoBytes(List<byte> bytes, int field, byte[] data)
    {
        ProtoVarint(bytes, (ulong)((field << 3) | 2));
        ProtoVarint(bytes, (ulong)data.Length);
        bytes.AddRange(data);
    }

    private static void ProtoString(List<byte> bytes, int field, string text) =>
        ProtoBytes(bytes, field, System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Sqlite_keeps_old_tables_and_commit_rationale_rules()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcp-index-commit-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new SqliteStore(StoreOptions.Sqlite(path), NullLogger<SqliteStore>.Instance);
        try
        {
            await ExerciseAsync(store);
        }
        finally
        {
            await store.DisposeAsync();
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
            {
                File.Delete(file);
            }
        }
    }

    [EnvironmentFact(PostgresVariable)]
    public async Task Postgres_keeps_old_tables_and_commit_rationale_rules()
    {
        var schema = "mcp_idx_" + Guid.NewGuid().ToString("N")[..8];
        var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(PostgresVariable)!) { SearchPath = schema };
        await using (var admin = new NpgsqlConnection(builder.ConnectionString))
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync($"CREATE SCHEMA {schema}");
        }

        var store = new PostgresStore(new StoreOptions(StoreKind.Postgres, builder.ConnectionString), NullLogger<PostgresStore>.Instance);
        try
        {
            await ExerciseAsync(store);
        }
        finally
        {
            await store.DisposeAsync();
            await using var admin = new NpgsqlConnection(builder.ConnectionString);
            await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {schema} CASCADE");
        }
    }

    private static async Task ExerciseAsync(IKnowledgeStore store)
    {
        await store.InitializeAsync(IndexSchema.Migrations);
        Assert.Equal(2, store.SchemaVersion);
        await using var connection = await store.OpenAsync();
        var versions = await connection.QueryAsync("SELECT version, name FROM schema_version ORDER BY version", r => (r.GetInt32("version"), r.GetString("name")));
        Assert.Equal([(1, "initial"), (2, "commit_knowledge")], versions);
        await AssertEmbeddingsStayBytesAsync(connection, store.Kind);
        Assert.Equal(0, await connection.ScalarAsync<long>("SELECT COUNT(*) FROM symbols"));
        Assert.Equal(0, await connection.ScalarAsync<long>("SELECT COUNT(*) FROM symbol"));

        var root = Path.Combine(Path.GetTempPath(), "mcp-index-commit-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Pipe.cs"), Source);
            await Git(root, "init", "-q");
            await Git(root, "add", "-A");
            await Git(root, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "-m", "a");
            var defaultBranch = (await GitOut(root, "rev-parse", "--abbrev-ref", "HEAD")).Trim();
            var shaA = (await GitOut(root, "rev-parse", "HEAD")).Trim();

            var knowledge = new CommitKnowledge(store, new IndexRepository(store), new IndexOptions(), NullLogger<CommitKnowledge>.Instance);
            var identity = RepoIdentity.FromPath(root);
            var indexed = await knowledge.ReindexAsync(identity, shaA, CancellationToken.None);
            Assert.True(indexed.Completed);
            Assert.Equal("syntax", indexed.Source);
            Assert.False(indexed.Merge);
            Assert.True(indexed.Symbols >= 4);

            var kinds = await connection.QueryAsync(
                "SELECT kind FROM symbol_edge WHERE repo_id = @repoId AND commit_sha = @sha",
                r => r.GetString("kind"),
                new { repoId = identity.RepoId, sha = shaA });
            Assert.Contains("calls", kinds);
            Assert.Contains("implements", kinds);
            Assert.Contains("references", kinds);

            var whyMissing = await knowledge.WhyAsync(identity, "IPipe", shaA, false, null, CancellationToken.None);
            Assert.True(whyMissing.Unknown);
            Assert.DoesNotContain("must not become", string.Join('\n', whyMissing.Rationale.Select(r => r.Text)), StringComparison.Ordinal);

            var saved = await knowledge.UpsertAsync(identity, "Main", "version A is the entry.", "high", "tester", shaA, "rule-1", null, false, "Pipe.cs", 1, 1, CancellationToken.None);
            Assert.Equal("active", saved.Status);
            await connection.ExecuteAsync(
                $"INSERT INTO business_rule (repo_id, rule_id, statement, status, created_at) VALUES (@repoId, 'rule-1', 'One entry point.', 'active', {store.Dialect.NowMs})",
                new { repoId = identity.RepoId });
            await connection.ExecuteAsync(
                $"INSERT INTO line_span (repo_id, anchor_key, path, start_line, end_line, created_at) VALUES (@repoId, @anchor, 'Pipe.cs', 1, 2, {store.Dialect.NowMs})",
                new { repoId = identity.RepoId, anchor = saved.Anchor });

            var again = await knowledge.ReindexAsync(identity, shaA, CancellationToken.None);
            Assert.Equal(0, again.RationaleMarkedStale);
            Assert.Equal(1, await connection.ScalarAsync<long>("SELECT COUNT(*) FROM line_span WHERE repo_id = @repoId", new { repoId = identity.RepoId }));
            Assert.Equal("active", await StatusAsync(connection, saved.Id));

            var blast = await knowledge.BlastAsync(identity, "Main", shaA, CancellationToken.None);
            Assert.Contains("Not a proof", blast.Hint, StringComparison.Ordinal);
            Assert.Contains(blast.Symbols, s => s.Name == "Flush");
            Assert.Contains(blast.Symbols, s => s.Name == "Pipe");
            var mainSymbol = blast.Symbols.Single(s => s.Name == "Main");
            Assert.Contains(mainSymbol.Rules, r => r.RuleId == "rule-1" && r.Statement == "One entry point.");
            var pipe = await knowledge.BlastAsync(identity, "Pipe", shaA, CancellationToken.None);
            Assert.Contains(pipe.Symbols, s => s.Name == "IPipe");

            await WriteMain(root, "version B");
            await Git(root, "checkout", "-q", "-b", "feature");
            await Git(root, "add", "-A");
            await Git(root, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "-m", "b");
            var shaB = (await GitOut(root, "rev-parse", "HEAD")).Trim();
            var indexedB = await knowledge.ReindexAsync(identity, shaB, CancellationToken.None);
            Assert.False(indexedB.Merge);
            Assert.Equal(0, indexedB.RationaleMarkedStale);
            Assert.Equal("active", await StatusAsync(connection, saved.Id));
            var whyA = await knowledge.WhyAsync(identity, "Main", shaA, false, null, CancellationToken.None);
            Assert.Equal("version A is the entry.", Assert.Single(whyA.Rationale).Text);
            var whyB = await knowledge.WhyAsync(identity, "Main", shaB, false, null, CancellationToken.None);
            Assert.True(whyB.Unknown);

            await Assert.ThrowsAsync<McpServices.Hosting.ToolException>(() => knowledge.UpsertAsync(identity, "Main", "wrong hash", "high", "tester", shaB, "rule-1", "not-the-hash", false, null, null, null, CancellationToken.None));
            var forced = await knowledge.UpsertAsync(identity, "Main", "stored stale on purpose", "low", "tester", shaB, "rule-1", "not-the-hash", true, null, null, null, CancellationToken.None);
            Assert.Equal("stale", forced.Status);
            Assert.False(forced.SupersededPrevious);
            Assert.Equal("active", await StatusAsync(connection, saved.Id));

            var branch = await knowledge.UpsertAsync(identity, "Main", "version B replaced the comment.", "high", "tester", shaB, "rule-1", null, false, null, null, null, CancellationToken.None);
            Assert.Equal("active", branch.Status);
            Assert.True(branch.SupersededPrevious);
            Assert.Equal("superseded", await StatusAsync(connection, saved.Id));
            Assert.Equal("version A is the entry.", Assert.Single((await knowledge.WhyAsync(identity, "Main", shaA, false, null, CancellationToken.None)).Rationale).Text);
            Assert.Equal("version B replaced the comment.", Assert.Single((await knowledge.WhyAsync(identity, "Main", shaB, false, null, CancellationToken.None)).Rationale).Text);
            Assert.Equal(1, await connection.ScalarAsync<long>("SELECT COUNT(*) FROM rationale WHERE repo_id = @repoId AND anchor_key = @anchor AND rule_id = 'rule-1' AND status = 'active'", new { repoId = identity.RepoId, anchor = saved.Anchor }));

            await Git(root, "checkout", "-q", defaultBranch);
            await WriteMain(root, "version main");
            await Git(root, "add", "-A");
            await Git(root, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "-m", "main");
            var merge = await GitRaw(root, "merge", "--no-edit", "feature");
            Assert.False(merge);
            await WriteMain(root, "version merged");
            await Git(root, "add", "-A");
            await Git(root, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "-m", "merge");
            var shaM = (await GitOut(root, "rev-parse", "HEAD")).Trim();
            var indexedM = await knowledge.ReindexAsync(identity, shaM, CancellationToken.None);
            Assert.True(indexedM.Merge);
            Assert.True(indexedM.RationaleMarkedStale >= 2);
            Assert.Equal("stale", await StatusAsync(connection, saved.Id));
            Assert.Equal("stale", await StatusAsync(connection, branch.Id));
            Assert.True((await knowledge.WhyAsync(identity, "Main", shaM, false, null, CancellationToken.None)).Unknown);
            var stale = await knowledge.WhyAsync(identity, "Main", shaM, true, null, CancellationToken.None);
            Assert.Contains(stale.Rationale, r => r.Text == "version A is the entry." && r.Status == "stale");
            Assert.Contains(stale.Rationale, r => r.Text == "version B replaced the comment." && r.Status == "stale");
            Assert.Equal(1, await connection.ScalarAsync<long>("SELECT COUNT(*) FROM line_span WHERE repo_id = @repoId", new { repoId = identity.RepoId }));

            var other = Path.Combine(Path.GetTempPath(), "mcp-index-commit-other-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(other);
            try
            {
                await File.WriteAllTextAsync(Path.Combine(other, "Pipe.cs"), Source);
                await Git(other, "init", "-q");
                await Git(other, "add", "-A");
                await Git(other, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "-m", "a");
                var otherIdentity = RepoIdentity.FromPath(other);
                Assert.NotEqual(identity.RepoId, otherIdentity.RepoId);
                await knowledge.ReindexAsync(otherIdentity, null, CancellationToken.None);
                Assert.True((await knowledge.WhyAsync(otherIdentity, "Main", null, false, null, CancellationToken.None)).Unknown);
            }
            finally
            {
                DeleteTree(other);
            }

            await AssertRejectedByDatabaseAsync(connection, store, identity.RepoId);
            var notesBefore = await connection.ScalarAsync<long>("SELECT COUNT(*) FROM rationale WHERE repo_id = @repoId", new { repoId = identity.RepoId });
            await new IndexRepository(store).DeleteRepositoryAsync(connection, identity.RepoId, CancellationToken.None);
            Assert.Equal(notesBefore, await connection.ScalarAsync<long>("SELECT COUNT(*) FROM rationale WHERE repo_id = @repoId", new { repoId = identity.RepoId }));
            Assert.Equal(0, await connection.ScalarAsync<long>("SELECT COUNT(*) FROM notes WHERE repo_id = @repoId", new { repoId = identity.RepoId }));
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static async Task AssertEmbeddingsStayBytesAsync(DbConnection connection, StoreKind kind)
    {
        if (kind == StoreKind.Sqlite)
        {
            var type = await connection.ScalarAsync<string>("SELECT type FROM pragma_table_info('embeddings') WHERE name = 'vector'");
            Assert.Equal("BLOB", type);
            Assert.Equal(0, await connection.ScalarAsync<long>("SELECT COUNT(*) FROM pragma_table_info('symbols') WHERE name = 'commit_sha'"));
            Assert.Equal(1, await connection.ScalarAsync<long>("SELECT COUNT(*) FROM pragma_table_info('symbol') WHERE name = 'commit_sha'"));
            return;
        }

        var dataType = await connection.ScalarAsync<string>("SELECT data_type FROM information_schema.columns WHERE table_name = 'embeddings' AND column_name = 'vector' AND table_schema = current_schema()");
        Assert.Equal("bytea", dataType);
    }

    private static async Task AssertRejectedByDatabaseAsync(DbConnection connection, IKnowledgeStore store, string repoId)
    {
        var ok = await InsertRawAsync(connection, store, repoId, "C# stays in the sentence.", "high", "tester", "anchor-ok", "stale");
        Assert.Equal(1, ok);
        await Assert.ThrowsAnyAsync<DbException>(() => InsertRawAsync(connection, store, repoId, "# Heading", "high", "tester", "anchor-bad", "stale"));
        await Assert.ThrowsAnyAsync<DbException>(() => InsertRawAsync(connection, store, repoId, "line\n## Next", "high", "tester", "anchor-bad", "stale"));
        await Assert.ThrowsAnyAsync<DbException>(() => InsertRawAsync(connection, store, repoId, new string('a', 501), "high", "tester", "anchor-bad", "stale"));
        await Assert.ThrowsAnyAsync<DbException>(() => InsertRawAsync(connection, store, repoId, "fine", "  ", "tester", "anchor-bad", "stale"));
        await Assert.ThrowsAnyAsync<DbException>(() => InsertRawAsync(connection, store, repoId, "fine", "high", "tester", "   ", "active"));
        await InsertRawAsync(connection, store, repoId, "first active", "high", "tester", "anchor-unique", "active");
        await Assert.ThrowsAnyAsync<DbException>(() => InsertRawAsync(connection, store, repoId, "second active", "high", "tester", "anchor-unique", "active"));
    }

    private static async Task<int> InsertRawAsync(DbConnection connection, IKnowledgeStore store, string repoId, string body, string confidence, string source, string anchor, string status) =>
        await connection.ExecuteAsync(
            $"INSERT INTO rationale (repo_id, anchor_key, rule_id, commit_sha, body, confidence, source, status, created_at) VALUES (@repoId, @anchor, '', 'abc', @body, @confidence, @source, @status, {store.Dialect.NowMs})",
            new { repoId, anchor, body, confidence, source, status });

    private static async Task<string> StatusAsync(DbConnection connection, long id) =>
        await connection.ScalarAsync<string>("SELECT status FROM rationale WHERE id = @id", new { id }) ?? string.Empty;

    private static async Task WriteMain(string root, string version)
    {
        var text = await File.ReadAllTextAsync(Path.Combine(root, "Pipe.cs"));
        var updated = System.Text.RegularExpressions.Regex.Replace(text, "// version [^\n]+", "// version " + version);
        await File.WriteAllTextAsync(Path.Combine(root, "Pipe.cs"), updated);
    }

    private static async Task Git(string root, params string[] args)
    {
        if (!await GitRaw(root, args))
        {
            throw new InvalidOperationException("git " + string.Join(' ', args) + " failed");
        }
    }

    private static async Task<bool> GitRaw(string root, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in McpServices.Hosting.ProcessOutput.GitBackgroundHelpersOff)
        {
            info.Environment[key] = value;
        }

        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        return process.ExitCode == 0;
    }

    private static async Task<string> GitOut(string root, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(await process.StandardError.ReadToEndAsync());
        }

        return output;
    }

    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }
}
