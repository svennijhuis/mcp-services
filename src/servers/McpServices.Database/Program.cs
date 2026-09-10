using McpServices.Database;
using McpServices.Hosting;
using Microsoft.Extensions.DependencyInjection;

var descriptor = new ServerDescriptor("mcp-database", "Query and inspect SQLite, PostgreSQL and SQL Server databases: schema discovery, parameterised read queries, explain plans, CSV/JSON export and (unless --read-only) write statements.")
{
    Instructions = "Start with list_databases and get_schema_overview, then use read_query with @name parameters. Only a single statement per call is allowed. write_query is absent when the server runs read-only.",
    Flags = ["read-only"],
    Usage = """
        Usage: mcp-database [transport options] [--read-only] --db <alias>=<provider>:<connection-string> [--db ...]

        Providers:
          sqlite     connection is a file path or a Microsoft.Data.Sqlite connection string
          postgres   Npgsql connection string, e.g. Host=localhost;Database=app;Username=app;Password=...
          sqlserver  Microsoft.Data.SqlClient connection string

        Databases can also be registered through environment variables: MCP_DB__<alias>=<provider>:<connection-string>.
        --read-only (or MCP_DB_READ_ONLY=true) hides write_query and rejects all modifying statements.
        """,
};

return await McpServerHost.RunAsync(args, descriptor, context =>
{
    var definitions = context.Args.GetOptions("db").Concat(DatabaseRegistry.FromEnvironment()).ToList();
    var readOnly = context.Args.HasFlag("read-only")
        || string.Equals(Environment.GetEnvironmentVariable("MCP_DB_READ_ONLY"), "true", StringComparison.OrdinalIgnoreCase)
        || Environment.GetEnvironmentVariable("MCP_DB_READ_ONLY") == "1";

    var registry = new DatabaseRegistry(definitions, readOnly);
    context.Expose("readOnly", registry.ReadOnly);
    context.Expose("databases", registry.All.Select(p => new { alias = p.Alias, engine = p.Kind, connection = p.RedactedConnectionString }).ToList());

    context.Services.AddSingleton(registry);
    context.Mcp.WithTools<DatabaseReadTools>(ToolJson.Options);
    if (!registry.ReadOnly)
    {
        context.Mcp.WithTools<DatabaseWriteTools>(ToolJson.Options);
    }

    context.Mcp.WithResources<DatabaseResources>();
});
