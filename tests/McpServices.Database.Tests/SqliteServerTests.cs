using System.Text.Json;
using McpServices.TestSupport;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;

namespace McpServices.Database.Tests;

/// <summary>One writable server process shared by all tests in the class; the database is seeded once.</summary>
public sealed class SqliteServerFixture : IAsyncLifetime
{
    public string DatabasePath { get; } = Path.Combine(Path.GetTempPath(), "mcp-db-tests-" + Guid.NewGuid().ToString("N") + ".db");

    public ServerFixture Server { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Seed(DatabasePath);
        Server = await ServerFixture.StartAsync("McpServices.Database", ["--db", $"main=sqlite:{DatabasePath}"]);
    }

    public async Task DisposeAsync()
    {
        await Server.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(DatabasePath);
    }

    public static void Seed(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, email TEXT UNIQUE, created_at TEXT DEFAULT CURRENT_TIMESTAMP);
            CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER NOT NULL REFERENCES customers(id), total REAL NOT NULL, note TEXT);
            CREATE INDEX ix_orders_customer ON orders(customer_id);
            CREATE VIEW customer_totals AS SELECT c.name, SUM(o.total) AS total FROM customers c JOIN orders o ON o.customer_id = c.id GROUP BY c.name;
            INSERT INTO customers (id, name, email) VALUES (1, 'Ada', 'ada@example.com'), (2, 'Grace', 'grace@example.com'), (3, 'Linus', NULL);
            INSERT INTO orders (customer_id, total, note) VALUES (1, 10.5, 'first'), (1, 20, NULL), (2, 99.99, 'has, comma');
            """;
        command.ExecuteNonQuery();
    }
}

public class SqliteServerTests(SqliteServerFixture fixture) : IClassFixture<SqliteServerFixture>
{
    private ServerFixture Server => fixture.Server;

    [Fact]
    public async Task Exposes_read_and_write_tools()
    {
        var tools = await Server.ToolNamesAsync();
        Assert.Equal(["describe_table", "explain_query", "export_query", "get_schema_overview", "list_databases", "list_tables", "read_query", "server_info", "write_query"], tools);
    }

    [Fact]
    public async Task List_databases_reports_engine_and_status()
    {
        var result = await Server.CallJsonAsync("list_databases");
        Assert.False(result.GetProperty("readOnly").GetBoolean());
        var db = Assert.Single(result.GetProperty("databases").EnumerateArray());
        Assert.Equal("main", db.GetProperty("alias").GetString());
        Assert.Equal("sqlite", db.GetProperty("engine").GetString());
        Assert.Equal("reachable", db.GetProperty("status").GetString());
    }

    [Fact]
    public async Task List_tables_includes_views_and_supports_search()
    {
        var all = await Server.CallJsonAsync("list_tables");
        var names = all.GetProperty("tables").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("customers", names);
        Assert.Contains("orders", names);
        Assert.Contains("customer_totals", names);

        var filtered = await Server.CallJsonAsync("list_tables", new { search = "order" });
        var single = Assert.Single(filtered.GetProperty("tables").EnumerateArray());
        Assert.Equal("orders", single.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Describe_table_returns_columns_keys_and_indexes()
    {
        var schema = await Server.CallJsonAsync("describe_table", new { table = "orders" });
        var columns = schema.GetProperty("columns").EnumerateArray().ToList();
        Assert.Equal(["id", "customer_id", "total", "note"], columns.Select(c => c.GetProperty("name").GetString()));
        Assert.True(columns[0].GetProperty("primaryKey").GetBoolean());
        Assert.False(columns[1].GetProperty("nullable").GetBoolean());
        Assert.True(columns[3].GetProperty("nullable").GetBoolean());

        var fk = Assert.Single(schema.GetProperty("foreignKeys").EnumerateArray());
        Assert.Equal("customer_id", fk.GetProperty("column").GetString());
        Assert.Equal("customers", fk.GetProperty("referencedTable").GetString());

        Assert.Contains(schema.GetProperty("indexes").EnumerateArray(), i => i.GetProperty("name").GetString() == "ix_orders_customer");
    }

    [Fact]
    public async Task Describe_table_rejects_unknown_and_unsafe_names()
    {
        Assert.Contains("does not exist", await Server.CallExpectingErrorAsync("describe_table", new { table = "missing" }), StringComparison.Ordinal);
        Assert.Contains("plain identifier", await Server.CallExpectingErrorAsync("describe_table", new { table = "orders; DROP TABLE orders" }), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Schema_overview_lists_every_table_with_columns()
    {
        var overview = await Server.CallJsonAsync("get_schema_overview");
        Assert.Equal(3, overview.GetProperty("tableCount").GetInt32());
        var customers = overview.GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("table").GetString() == "customers");
        Assert.Contains(customers.GetProperty("columns").EnumerateArray(), c => c.GetString()!.StartsWith("id INTEGER PK", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Read_query_binds_named_parameters_and_caps_rows()
    {
        var result = await Server.CallJsonAsync("read_query", new
        {
            sql = "SELECT id, name, email FROM customers WHERE id >= @min ORDER BY id",
            @params = new { min = 2 },
        });

        Assert.Equal(2, result.GetProperty("rowCount").GetInt32());
        Assert.False(result.GetProperty("truncated").GetBoolean());
        Assert.Equal(["id", "name", "email"], result.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        var rows = result.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal("Grace", rows[0][1].GetString());
        Assert.Equal(JsonValueKind.Null, rows[1][2].ValueKind);

        var capped = await Server.CallJsonAsync("read_query", new { sql = "SELECT id FROM customers ORDER BY id", maxRows = 1 });
        Assert.Equal(1, capped.GetProperty("rowCount").GetInt32());
        Assert.True(capped.GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData("DELETE FROM customers")]
    [InlineData("SELECT 1; DELETE FROM customers")]
    [InlineData("WITH x AS (SELECT 1) INSERT INTO customers (name) VALUES ('evil')")]
    [InlineData("PRAGMA journal_mode = DELETE")]
    public async Task Read_query_rejects_modifying_statements(string sql)
    {
        var error = await Server.CallExpectingErrorAsync("read_query", new { sql });
        Assert.NotEmpty(error);

        var count = await Server.CallJsonAsync("read_query", new { sql = "SELECT count(*) FROM customers" });
        Assert.Equal(3, count.GetProperty("rows")[0][0].GetInt32());
    }

    [Fact]
    public async Task Read_query_reports_sql_errors_as_tool_errors()
    {
        var error = await Server.CallExpectingErrorAsync("read_query", new { sql = "SELECT * FROM nope" });
        Assert.Contains("no such table", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Write_query_modifies_data_and_rejects_reads()
    {
        var insert = await Server.CallJsonAsync("write_query", new
        {
            sql = "INSERT INTO orders (customer_id, total, note) VALUES (@customer, @total, @note)",
            @params = new { customer = 3, total = 5.25, note = "written by test" },
        });
        Assert.Equal(1, insert.GetProperty("affectedRows").GetInt32());

        var check = await Server.CallJsonAsync("read_query", new { sql = "SELECT total FROM orders WHERE note = @note", @params = new { note = "written by test" } });
        Assert.Equal(5.25, check.GetProperty("rows")[0][0].GetDouble());

        var delete = await Server.CallJsonAsync("write_query", new { sql = "DELETE FROM orders WHERE note = @note", @params = new { note = "written by test" } });
        Assert.Equal(1, delete.GetProperty("affectedRows").GetInt32());

        Assert.Contains("read_query", await Server.CallExpectingErrorAsync("write_query", new { sql = "SELECT 1" }), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explain_query_returns_a_plan()
    {
        var result = await Server.CallJsonAsync("explain_query", new { sql = "SELECT * FROM orders WHERE customer_id = @c", @params = new { c = 1 } });
        Assert.Contains("ix_orders_customer", result.GetProperty("plan").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_query_produces_csv_and_json()
    {
        var csv = await Server.CallTextAsync("export_query", new { sql = "SELECT id, note FROM orders WHERE id <= 3 ORDER BY id", format = "csv" });
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("id,note", lines[0]);
        Assert.Equal("1,first", lines[1]);
        Assert.Equal("2,", lines[2]);
        Assert.Equal("3,\"has, comma\"", lines[3]);

        var json = await Server.CallTextAsync("export_query", new { sql = "SELECT id, name FROM customers ORDER BY id", format = "json" });
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(3, doc.RootElement.GetArrayLength());
        Assert.Equal("Ada", doc.RootElement[0].GetProperty("name").GetString());

        Assert.Contains("csv", await Server.CallExpectingErrorAsync("export_query", new { sql = "SELECT 1", format = "xml" }), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Schema_resource_serialises_the_whole_database()
    {
        var templates = await Server.Client.ListResourceTemplatesAsync();
        Assert.Contains(templates, t => t.UriTemplate == "db://{alias}/schema");

        var resource = await Server.Client.ReadResourceAsync("db://main/schema");
        var text = Assert.IsType<TextResourceContents>(Assert.Single(resource.Contents));
        using var doc = JsonDocument.Parse(text.Text);
        Assert.Equal("sqlite", doc.RootElement.GetProperty("engine").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("tables").GetArrayLength());
    }

    [Fact]
    public async Task Server_info_exposes_registered_databases_without_secrets()
    {
        var info = await Server.CallJsonAsync("server_info");
        Assert.Equal("mcp-database", info.GetProperty("name").GetString());
        var databases = info.GetProperty("configuration").GetProperty("databases");
        Assert.Equal("main", databases[0].GetProperty("alias").GetString());
    }
}

public class ReadOnlyServerTests
{
    [Fact]
    public async Task Read_only_mode_hides_write_query_and_uses_environment_registration()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcp-db-ro-" + Guid.NewGuid().ToString("N") + ".db");
        SqliteServerFixture.Seed(path);
        try
        {
            await using var server = await ServerFixture.StartAsync(
                "McpServices.Database",
                ["--read-only"],
                new Dictionary<string, string?> { ["MCP_DB__reports"] = $"sqlite:{path}" });

            var tools = await server.ToolNamesAsync();
            Assert.DoesNotContain("write_query", tools);
            Assert.Contains("read_query", tools);

            var databases = await server.CallJsonAsync("list_databases");
            Assert.True(databases.GetProperty("readOnly").GetBoolean());
            Assert.Equal("reports", databases.GetProperty("databases")[0].GetProperty("alias").GetString());

            var rows = await server.CallJsonAsync("read_query", new { sql = "SELECT count(*) FROM orders", database = "reports" });
            Assert.Equal(3, rows.GetProperty("rows")[0][0].GetInt32());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
