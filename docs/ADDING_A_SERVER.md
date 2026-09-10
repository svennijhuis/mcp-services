# Adding a server

A server is a console project under `src/servers/` that references `McpServices.Hosting`, declares its tools with SDK attributes and calls `McpServerHost.RunAsync`. Everything else (transports, logging, `server_info`, packaging as a tool, CI) comes from the shared build files.

## 1. Project

`src/servers/McpServices.Example/McpServices.Example.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsServerProject>true</IsServerProject>
    <AssemblyName>McpServices.Example</AssemblyName>
    <RootNamespace>McpServices.Example</RootNamespace>
    <ToolCommandName>mcp-example</ToolCommandName>
    <PackageId>McpServices.Example</PackageId>
    <Description>MCP server for ...</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="../../shared/McpServices.Hosting/McpServices.Hosting.csproj" />
    <!-- <ProjectReference Include="../../shared/McpServices.Storage/McpServices.Storage.csproj" /> when the server needs a SQLite/PostgreSQL store -->
  </ItemGroup>

</Project>
```

`IsServerProject=true` makes `Directory.Build.targets` turn it into an executable, packable .NET tool with `RollForward=LatestMajor`. Package versions live in `Directory.Packages.props` (central package management), so add new packages there and reference them without a version.

Add the project (and its test project) to `McpServices.slnx`, and the tool command to `scripts/servers.sh`, `scripts/install-tools.ps1` and `docker/docker-compose.yml`.

## 2. Program.cs

```csharp
using McpServices.Example;
using McpServices.Hosting;
using Microsoft.Extensions.DependencyInjection;

var descriptor = new ServerDescriptor("mcp-example", "One sentence on what the server does.")
{
    Instructions = "Hints the client shows the model: which tool to start with, conventions (dryRun, pageToken).",
    Flags = ["read-only"],                       // boolean options; everything else is --name value
    Usage = """
        Usage: mcp-example [transport options] [--root <dir>] [--read-only]

        Options:
          --root <dir>     ...
          --read-only      ...
        """,
};

return await McpServerHost.RunAsync(args, descriptor, context =>
{
    var options = ExampleOptions.From(context.Args);   // CommandLine: GetOption(name, envVar), GetOptions, GetInt, HasFlag, Positionals
    context.Expose("root", options.Root);              // shows up in server_info
    context.Services.AddSingleton(options);
    context.Services.AddSingleton<ExampleService>();
    context.Mcp.WithTools<ExampleTools>(ToolJson.Options);
    // context.Mcp.WithResources<ExampleResources>(); context.Mcp.WithPrompts<ExamplePrompts>();
});
```

Throw `ServerStartupException` from option parsing for configuration errors; the host prints usage plus the message and exits with code 2.

## 3. Tools

```csharp
[McpServerToolType]
public sealed class ExampleTools(ExampleService service)
{
    [McpServerTool(Name = "do_thing", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Do thing")]
    [Description("What it does, when to use it, what it returns. This text is what the model reads.")]
    public async Task<object> DoThing(
        [Description("What this parameter means, with an example.")] string name,
        [Description("Page token from a previous call.")] string? pageToken = null,
        [Description("Page size (default 50, max 500).")] int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        ToolGuard.NotEmpty(name, "name");
        var items = await service.FindAsync(name, cancellationToken).ConfigureAwait(false);
        return Paging.Page(items, pageToken, pageSize);
    }
}
```

Conventions that keep the servers consistent:

- Validate with `ToolGuard` and throw `ToolException` with a message that tells the model what to do instead. Never let raw exceptions escape for expected failures.
- Return anonymous objects/records; `ToolJson.Options` (camelCase, lower-case enums, nulls omitted) is applied through `WithTools<T>(ToolJson.Options)`.
- Anything that can be large gets `pageToken`/`pageSize` and an upper cap.
- Anything that writes has `dryRun = true` by default and returns a unified diff (`TextDiff.Unified(before, after, path)`), or a clearly destructive name plus `Destructive = true`.
- Read secrets from environment variables, never from tool parameters or the command line, and redact them in output (`SecretRedactor` in Storage).
- Log with `ILogger<T>`; it goes to stderr (stdio) or the console (HTTP).

Resources use `[McpServerResourceType]` + `[McpServerResource(UriTemplate = "example://{id}", Name = ..., MimeType = ...)]` and return a string; prompts use `[McpServerPromptType]` + `[McpServerPrompt(Name = ...)]`.

## 4. Storage (optional)

```csharp
var store = KnowledgeStoreFactory.Resolve(context, "mcp-example", "example.db");   // --store sqlite:<file>|postgres:<conn>, env MCP_EXAMPLE_STORE is your call
context.AddKnowledgeStore(store, ExampleSchema.Migrations);
```

Write each migration once per dialect (`Migration(1, "init", sqliteSql, postgresSql)`) and use the `Db` extension methods (`connection.QueryAsync(sql, new { id })`, `ExecuteAsync`, `ScalarAsync<T>`) so the code runs unchanged on both. Take `StoreInitializer` as a dependency and call `StoreAsync()` before opening connections; it applies pending migrations exactly once.

## 5. Tests

`tests/McpServices.Example.Tests/McpServices.Example.Tests.csproj` references the server project and `McpServices.TestSupport`. Test through the real protocol:

```csharp
public sealed class ExampleServerTests : IAsyncLifetime
{
    private ServerFixture _server = null!;

    public async Task InitializeAsync() => _server = await ServerFixture.StartAsync("McpServices.Example", ["--root", Path.GetTempPath()]);

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task Lists_tools()
    {
        Assert.Contains("do_thing", await _server.ToolNamesAsync());
        var result = await _server.CallJsonAsync("do_thing", new { name = "x" });
        Assert.Equal(0, result.GetProperty("totalCount").GetInt32());
        var error = await _server.CallExpectingErrorAsync("do_thing", new { name = "" });
        Assert.Contains("required", error);
    }
}
```

Fake external services with `HttpListener` on a loopback port (see the Learnings and Index tests) and gate tests that need real infrastructure with `[EnvironmentFact("MCP_TEST_...")]`.

## 6. Ship it

- `scripts/build.sh` must pass (warnings are errors; the analyzer set is in `Directory.Build.props`).
- Add `docs/servers/example.md` with options, tools and configuration snippets, link it from `README.md`, and add the server to `examples/*.json`.
- Add a compose service in `docker/docker-compose.yml` (generic `docker/Dockerfile` with `SERVER=McpServices.Example`).
- Credit any reference implementation in `THIRD_PARTY_NOTICES.md`.
