using McpServices.Hosting;
using McpServices.Storage;

namespace McpServices.Storage.Tests;

public class StoreOptionsTests
{
    [Theory]
    [InlineData("sqlite:/tmp/x/index.db", StoreKind.Sqlite)]
    [InlineData("/tmp/x/index.db", StoreKind.Sqlite)]
    [InlineData("relative/index.db", StoreKind.Sqlite)]
    [InlineData("postgres:Host=db;Username=u;Password=p;Database=d", StoreKind.Postgres)]
    [InlineData("pg:Host=db", StoreKind.Postgres)]
    public void Parses_store_definitions(string definition, StoreKind kind)
    {
        Assert.Equal(kind, StoreOptions.Parse(definition).Kind);
    }

    [Fact]
    public void Sqlite_paths_become_absolute_connection_strings()
    {
        var options = StoreOptions.Parse("sqlite:relative/index.db");
        Assert.Contains("Data Source=", options.ConnectionString, StringComparison.Ordinal);
        Assert.True(Path.IsPathRooted(options.Redacted));
        Assert.EndsWith(Path.Combine("relative", "index.db"), options.Redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Postgres_redaction_hides_password()
    {
        var options = StoreOptions.Parse("postgres:Host=db;Username=u;Password=topsecret;Database=d");
        Assert.DoesNotContain("topsecret", options.Redacted, StringComparison.Ordinal);
        Assert.Contains("Host=db", options.Redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_unknown_schemes()
    {
        Assert.Throws<ServerStartupException>(() => StoreOptions.Parse("mongodb://x"));
    }

    [Fact]
    public void Default_lives_under_data_home()
    {
        var options = StoreOptions.DefaultSqlite("mcp-index", "abc.db");
        Assert.EndsWith(Path.Combine("mcp-index", "abc.db"), options.Redacted, StringComparison.Ordinal);
        Assert.StartsWith(StoreOptions.DataHome, options.Redacted, StringComparison.Ordinal);
    }
}

public class SqlDialectTests
{
    [Fact]
    public void Sqlite_full_text_query_quotes_terms_and_prefixes_last()
    {
        Assert.Equal("\"order\" OR \"service\" OR \"submit\"*", SqlDialect.Sqlite.FullTextQuery("Order service: submit*"));
        Assert.Equal("\"orders\" OR \"submitted\"*", SqlDialect.Sqlite.FullTextQuery("where are orders submitted"));
        Assert.Equal("\"the\"*", SqlDialect.Sqlite.FullTextQuery("the"));
        Assert.Equal("\"\"", SqlDialect.Sqlite.FullTextQuery("  ***  "));
    }

    [Fact]
    public void Postgres_full_text_query_uses_tsquery_syntax()
    {
        Assert.Equal("order | service | submit:*", SqlDialect.Postgres.FullTextQuery("Order service submit"));
        Assert.Equal(string.Empty, SqlDialect.Postgres.FullTextQuery("!!"));
    }

    [Theory]
    [InlineData("GetUserToken", "GetUserToken get user token")]
    [InlineData("user_token", "user_token user token")]
    [InlineData("HTTPClient2", "HTTPClient2 http client 2")]
    [InlineData("plain", "plain")]
    public void Splits_identifiers(string input, string expected)
    {
        Assert.Equal(expected, SqlDialect.SplitIdentifiers(input));
    }

    [Fact]
    public void Upsert_clause_lists_updated_columns()
    {
        Assert.Equal("ON CONFLICT (id) DO UPDATE SET a = excluded.a, b = excluded.b", SqlDialect.Sqlite.Upsert("id", "a", "b"));
        Assert.Equal("ON CONFLICT (id) DO NOTHING", SqlDialect.Postgres.Upsert("id"));
    }
}

public class RrfTests
{
    [Fact]
    public void Fuses_rankings_and_rewards_agreement()
    {
        var fused = Rrf.Fuse(("keyword", (IReadOnlyList<string>)["a", "b", "c"]), ("vector", (IReadOnlyList<string>)["b", "d", "a"]));
        Assert.Equal(["b", "a", "d", "c"], fused.Select(f => f.Key).Take(4));
        Assert.Equal(2, fused[0].Ranks["keyword"]);
        Assert.Equal(1, fused[0].Ranks["vector"]);
        Assert.True(fused[0].Score > fused[1].Score);
    }

    [Fact]
    public void Weights_change_the_order()
    {
        var fused = Rrf.Fuse([("keyword", (IReadOnlyList<string>)["a", "b"], 1.0), ("boost", (IReadOnlyList<string>)["b"], 3.0)]);
        Assert.Equal("b", fused[0].Key);
    }
}

public class VectorCodecTests
{
    [Fact]
    public void Round_trips_and_scores()
    {
        float[] a = [1f, 0f, 0f, 2.5f, -1f];
        var bytes = VectorCodec.ToBytes(a);
        Assert.Equal(a, VectorCodec.FromBytes(bytes));
        Assert.Equal(1f, VectorCodec.Cosine(a, a), 5);
        Assert.Equal(0f, VectorCodec.Cosine([1f, 0f], [0f, 1f]), 5);
        Assert.Equal(0f, VectorCodec.Cosine([1f, 0f], [0f, 1f, 1f]));

        var big = Enumerable.Range(0, 37).Select(i => (float)i).ToArray();
        Assert.Equal(1f, VectorCodec.Cosine(big, big), 4);
        Assert.Equal(1f, VectorCodec.Normalize(big).Select(v => v * v).Sum(), 4);
    }
}

public class RepoIdentityTests
{
    [Fact]
    public void Same_path_gives_same_id_and_different_paths_differ()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        try
        {
            var a = RepoIdentity.FromPath(root);
            var b = RepoIdentity.FromPath(root + Path.DirectorySeparatorChar);
            var c = RepoIdentity.FromPath(Path.Combine(root, "sub", ".."));
            Assert.Equal(a.RepoId, b.RepoId);
            Assert.Equal(a.RepoId, c.RepoId);
            Assert.Equal(16, a.RepoId.Length);
            Assert.NotEqual(a.RepoId, RepoIdentity.FromPath(Path.Combine(root, "sub")).RepoId);
            Assert.Equal(Path.GetFileName(root), a.Name);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Content_hash_is_stable()
    {
        Assert.Equal(RepoIdentity.ContentHash("hello"u8), RepoIdentity.ContentHash("hello"u8));
        Assert.NotEqual(RepoIdentity.ContentHash("hello"u8), RepoIdentity.ContentHash("hellp"u8));
        Assert.Equal(32, RepoIdentity.ContentHash("x"u8).Length);
    }
}

public class SecretRedactorTests
{
    [Theory]
    [InlineData("token sk-abcdefghijklmnopqrstuvwxyz0123456789", "sk-abcdef")]
    [InlineData("gh token ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef012345", "ghp_")]
    [InlineData("Host=db;Username=app;Password=Sup3rSecret!;Database=x", "Sup3rSecret")]
    [InlineData("postgres://app:hunter2@db:5432/app", "hunter2")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz.0123456789", "abcdefghijklmnop")]
    [InlineData("export OPENAI_API_KEY=\"abcd1234efgh5678\"", "abcd1234")]
    [InlineData("aws AKIAIOSFODNN7EXAMPLE key", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0In0.abcdefghijklmnop done", "eyJhbGci")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIE\n-----END RSA PRIVATE KEY-----", "MIIE")]
    public void Redacts_known_secret_shapes(string input, string secretFragment)
    {
        var redacted = SecretRedactor.Redact(input)!;
        Assert.DoesNotContain(secretFragment, redacted, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Placeholder, redacted, StringComparison.Ordinal);
        Assert.True(SecretRedactor.ContainsSecret(input));
    }

    [Fact]
    public void Keeps_the_key_name_and_surrounding_text()
    {
        var redacted = SecretRedactor.Redact("use Password=abc123 for the db and then run migrations")!;
        Assert.Equal("use Password=[REDACTED] for the db and then run migrations", redacted);
        Assert.Equal("postgres://app:[REDACTED]@db/app", SecretRedactor.Redact("postgres://app:hunter2@db/app"));
    }

    [Theory]
    [InlineData("OrderService.Submit is the entry point, not the controller")]
    [InlineData("Run dotnet test before pushing; tests take ~40s")]
    [InlineData("The token bucket rate limiter resets every minute")]
    [InlineData("")]
    public void Leaves_normal_text_alone(string input)
    {
        Assert.Equal(input, SecretRedactor.Redact(input));
        Assert.False(SecretRedactor.ContainsSecret(input));
        Assert.Null(SecretRedactor.Redact(null));
    }
}
