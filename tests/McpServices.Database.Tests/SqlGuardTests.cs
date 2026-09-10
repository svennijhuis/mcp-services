using McpServices.Database;
using McpServices.Hosting;

namespace McpServices.Database.Tests;

public class SqlGuardTests
{
    [Theory]
    [InlineData("SELECT * FROM users")]
    [InlineData("  select id from users where id = @id; ")]
    [InlineData("WITH recent AS (SELECT * FROM orders) SELECT count(*) FROM recent")]
    [InlineData("EXPLAIN QUERY PLAN SELECT * FROM users")]
    [InlineData("PRAGMA table_info(users)")]
    [InlineData("PRAGMA journal_mode")]
    [InlineData("SHOW TABLES")]
    [InlineData("SELECT * FROM users ORDER BY created_at DESC")]
    [InlineData("SELECT * FROM users WHERE note = 'DROP TABLE users'")]
    [InlineData("SELECT * FROM users -- DELETE FROM users")]
    [InlineData("SELECT /* UPDATE users SET x = 1 */ id FROM users")]
    [InlineData("SELECT updated_at, deleted, inserted_by FROM audit")]
    [InlineData("SELECT $$DROP TABLE x$$ AS literal")]
    public void Classifies_read_statements(string sql)
    {
        Assert.Equal(SqlKind.Read, SqlGuard.Classify(sql));
    }

    [Theory]
    [InlineData("INSERT INTO users (name) VALUES ('a')")]
    [InlineData("update users set name = 'a'")]
    [InlineData("DELETE FROM users")]
    [InlineData("DROP TABLE users")]
    [InlineData("CREATE TABLE t (id int)")]
    [InlineData("WITH doomed AS (SELECT id FROM users) DELETE FROM users WHERE id IN (SELECT id FROM doomed)")]
    [InlineData("EXPLAIN ANALYZE UPDATE users SET name = 'x'")]
    [InlineData("SELECT * INTO backup FROM users")]
    [InlineData("PRAGMA journal_mode = WAL")]
    [InlineData("ATTACH DATABASE 'other.db' AS other")]
    [InlineData("VACUUM")]
    [InlineData("EXEC sp_who")]
    public void Classifies_write_statements(string sql)
    {
        Assert.Equal(SqlKind.Write, SqlGuard.Classify(sql));
    }

    [Theory]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("SELECT 1; DROP TABLE users;")]
    public void Rejects_multiple_statements(string sql)
    {
        var ex = Assert.Throws<ToolException>(() => SqlGuard.Classify(sql));
        Assert.Contains("single SQL statement", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-- only a comment")]
    [InlineData("/* nothing */")]
    public void Rejects_empty_statements(string sql)
    {
        Assert.Throws<ToolException>(() => SqlGuard.Classify(sql));
    }

    [Fact]
    public void EnsureRead_throws_for_writes()
    {
        var ex = Assert.Throws<ToolException>(() => SqlGuard.EnsureRead("DELETE FROM users"));
        Assert.Contains("write_query", ex.Message, StringComparison.Ordinal);
        SqlGuard.EnsureRead("SELECT 1");
    }

    [Fact]
    public void Strip_removes_comments_and_literal_contents()
    {
        var stripped = SqlGuard.Strip("SELECT 'it''s' , \"col\" , [bracket] FROM t -- DROP\n/* DELETE */ WHERE x = 1");
        Assert.DoesNotContain("DROP", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("it", stripped, StringComparison.Ordinal);
        Assert.Contains("WHERE x = 1", stripped, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("users", true)]
    [InlineData("public.users", true)]
    [InlineData("_t1$", true)]
    [InlineData("users; DROP TABLE x", false)]
    [InlineData("users\"", false)]
    [InlineData("a.b.c", false)]
    public void ValidateIdentifier_accepts_only_plain_names(string value, bool valid)
    {
        if (valid)
        {
            Assert.Equal(value, SqlGuard.ValidateIdentifier(value, "table"));
        }
        else
        {
            Assert.Throws<ToolException>(() => SqlGuard.ValidateIdentifier(value, "table"));
        }
    }
}
