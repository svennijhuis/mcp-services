using McpServices.Database;
using McpServices.Database.Providers;
using McpServices.Hosting;

namespace McpServices.Database.Tests;

public class DatabaseRegistryTests
{
    [Theory]
    [InlineData("main=sqlite:./app.db", "main", "sqlite")]
    [InlineData("Analytics=pg:Host=localhost;Database=x;Username=u;Password=p", "Analytics", "postgres")]
    [InlineData("crm=postgres:Host=db", "crm", "postgres")]
    [InlineData("legacy=mssql:Server=.;Database=Legacy;Integrated Security=true", "legacy", "sqlserver")]
    [InlineData("legacy-2=sqlserver:Server=.;Database=Legacy", "legacy-2", "sqlserver")]
    public void Parses_definitions(string definition, string alias, string kind)
    {
        var provider = DatabaseRegistry.Parse(definition);
        Assert.Equal(alias, provider.Alias);
        Assert.Equal(kind, provider.Kind);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("main=")]
    [InlineData("main=sqlite")]
    [InlineData("main=oracle:Data Source=x")]
    [InlineData("bad alias=sqlite:x.db")]
    public void Rejects_invalid_definitions(string definition)
    {
        Assert.Throws<ServerStartupException>(() => DatabaseRegistry.Parse(definition));
    }

    [Fact]
    public void Redacts_passwords()
    {
        var provider = DatabaseRegistry.Parse("crm=postgres:Host=db;Username=app;Password=s3cret;Database=crm");
        Assert.DoesNotContain("s3cret", provider.RedactedConnectionString, StringComparison.Ordinal);
        Assert.Contains("Host=db", provider.RedactedConnectionString, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_duplicate_aliases_and_empty_registries()
    {
        Assert.Throws<ServerStartupException>(() => new DatabaseRegistry(["a=sqlite:a.db", "A=sqlite:b.db"], readOnly: false));
        Assert.Throws<ServerStartupException>(() => new DatabaseRegistry([], readOnly: false));
    }

    [Fact]
    public void Get_resolves_single_default_and_unknown_aliases()
    {
        var single = new DatabaseRegistry(["main=sqlite:a.db"], readOnly: true);
        Assert.Equal("main", single.Get(null).Alias);
        Assert.Equal("main", single.Get("MAIN").Alias);
        Assert.Throws<ToolException>(() => single.Get("other"));

        var multi = new DatabaseRegistry(["a=sqlite:a.db", "b=sqlite:b.db"], readOnly: false);
        Assert.IsType<SqliteProvider>(multi.Get("b"));
        var ex = Assert.Throws<ToolException>(() => multi.Get(null));
        Assert.Contains("required", ex.Message, StringComparison.Ordinal);
    }
}
