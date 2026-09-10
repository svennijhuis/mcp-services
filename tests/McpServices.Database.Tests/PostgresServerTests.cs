using McpServices.Database.Providers;
using McpServices.TestSupport;
using Npgsql;

namespace McpServices.Database.Tests;

/// <summary>
/// Contract tests against a real PostgreSQL. Run with
/// <c>MCP_TEST_POSTGRES="Host=localhost;Username=postgres;Password=postgres;Database=postgres" dotnet test</c>
/// (for example against the compose stack in <c>docker/</c>).
/// </summary>
public class PostgresServerTests
{
    private const string Variable = "MCP_TEST_POSTGRES";

    [EnvironmentFact(Variable)]
    public async Task Discovers_schema_and_enforces_read_only_transactions()
    {
        var connectionString = Environment.GetEnvironmentVariable(Variable)!;
        var schema = "mcp_test_" + Guid.NewGuid().ToString("N")[..8];

        await using var admin = new NpgsqlConnection(connectionString);
        await admin.OpenAsync();
        await using (var setup = admin.CreateCommand())
        {
            setup.CommandText = $"""
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.customers (id serial PRIMARY KEY, name text NOT NULL, email text UNIQUE);
                CREATE TABLE {schema}.orders (id serial PRIMARY KEY, customer_id int NOT NULL REFERENCES {schema}.customers(id), total numeric(10,2) NOT NULL);
                INSERT INTO {schema}.customers (name, email) VALUES ('Ada', 'ada@example.com'), ('Grace', NULL);
                INSERT INTO {schema}.orders (customer_id, total) VALUES (1, 10.50), (1, 20.00);
                """;
            await setup.ExecuteNonQueryAsync();
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
            var provider = new PostgresProvider("pg", builder.ToString());

            var tables = await provider.ListTablesAsync(CancellationToken.None);
            Assert.Contains(tables, t => t.Schema == schema && t.Name == "orders");

            var described = await provider.DescribeTableAsync($"{schema}.orders", null, CancellationToken.None);
            Assert.Equal(["id", "customer_id", "total"], described.Columns.Select(c => c.Name));
            Assert.True(described.Columns[0].PrimaryKey);
            var fk = Assert.Single(described.ForeignKeys);
            Assert.Equal("customers", fk.ReferencedTable);

            var result = await provider.ExecuteReadAsync($"SELECT name FROM {schema}.customers WHERE id = @id", new Dictionary<string, object?> { ["id"] = 2 }, 10, CancellationToken.None);
            Assert.Equal("Grace", result.Rows[0][0]);

            // Read path must be rejected by PostgreSQL itself, even if SqlGuard were bypassed.
            await Assert.ThrowsAsync<PostgresException>(() => provider.ExecuteReadAsync($"DELETE FROM {schema}.orders", null, 10, CancellationToken.None));

            var write = await provider.ExecuteWriteAsync($"UPDATE {schema}.orders SET total = total + 1", null, CancellationToken.None);
            Assert.Equal(2, write.AffectedRows);

            var plan = await provider.ExplainAsync($"SELECT * FROM {schema}.orders WHERE customer_id = @c", new Dictionary<string, object?> { ["c"] = 1 }, CancellationToken.None);
            Assert.Contains("orders", plan, StringComparison.Ordinal);

            await using var server = await ServerFixture.StartAsync("McpServices.Database", ["--db", $"pg=postgres:{builder}"]);
            var overview = await server.CallJsonAsync("get_schema_overview");
            Assert.Equal("postgres", overview.GetProperty("engine").GetString());
        }
        finally
        {
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP SCHEMA {schema} CASCADE";
            await drop.ExecuteNonQueryAsync();
        }
    }
}
