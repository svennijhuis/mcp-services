using McpServices.Index.Indexing;

namespace McpServices.Index.Tests;

public class CSharpExtractorTests
{
    private const string Source = """
        using System;

        namespace Shop.Orders;

        /// <summary>
        /// Submits orders. See <see cref="IOrderRepository"/>.
        /// </summary>
        public sealed class OrderService(IOrderRepository repository) : IOrderService
        {
            private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

            public const int MaxItems = 100;

            /// <summary>Submits an order and returns its id.</summary>
            public async Task<Guid> SubmitAsync(Order order, CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(order);
                await repository.SaveAsync(order, cancellationToken);
                return order.Id;
            }

            public int Count { get; private set; }

            public string Name => "orders";
        }

        public interface IOrderService
        {
            Task<Guid> SubmitAsync(Order order, CancellationToken cancellationToken = default);
        }

        public record Order(Guid Id, decimal Total);

        public enum OrderState { New, Paid }
        """;

    [Fact]
    public void Extracts_types_members_signatures_and_docs()
    {
        var extraction = Extractor.Extract("Shop/OrderService.cs", "csharp", Source);
        var byName = extraction.Symbols.ToDictionary(s => s.FullName);

        var service = byName["Shop.Orders.OrderService"];
        Assert.Equal("class", service.Kind);
        Assert.Equal("Shop.Orders", service.Container);
        Assert.Equal("public sealed class OrderService(IOrderRepository repository) : IOrderService", service.Signature);
        Assert.Equal("Submits orders. See IOrderRepository.", service.Doc);
        Assert.Equal(8, service.StartLine);

        var submit = byName["Shop.Orders.OrderService.SubmitAsync"];
        Assert.Equal("method", submit.Kind);
        Assert.Equal("public async Task<Guid> SubmitAsync(Order order, CancellationToken cancellationToken = default)", submit.Signature);
        Assert.Equal("Submits an order and returns its id.", submit.Doc);
        Assert.Equal(15, submit.StartLine);
        Assert.Equal(20, submit.EndLine);

        Assert.Equal("field", byName["Shop.Orders.OrderService.Timeout"].Kind);
        Assert.Equal("private static readonly TimeSpan Timeout;", byName["Shop.Orders.OrderService.Timeout"].Signature);
        Assert.Equal("constant", byName["Shop.Orders.OrderService.MaxItems"].Kind);
        Assert.Equal("public int Count { get; private set; }", byName["Shop.Orders.OrderService.Count"].Signature);
        Assert.Equal("public string Name", byName["Shop.Orders.OrderService.Name"].Signature);
        Assert.Equal("interface", byName["Shop.Orders.IOrderService"].Kind);
        Assert.Equal("record", byName["Shop.Orders.Order"].Kind);
        Assert.Equal("enum", byName["Shop.Orders.OrderState"].Kind);
        Assert.Equal("enummember", byName["Shop.Orders.OrderState.Paid"].Kind);
    }

    [Fact]
    public void Chunks_follow_members()
    {
        var extraction = Extractor.Extract("Shop/OrderService.cs", "csharp", Source);
        var submit = Assert.Single(extraction.Chunks, c => c.Heading == "Shop.Orders.OrderService.SubmitAsync");
        Assert.Contains("repository.SaveAsync", submit.Text, StringComparison.Ordinal);
        Assert.Equal(15, submit.StartLine);
        Assert.Equal(Enumerable.Range(0, extraction.Chunks.Count), extraction.Chunks.Select(c => c.Ordinal));
    }

    [Fact]
    public void Broken_code_still_yields_something()
    {
        var extraction = Extractor.Extract("x.cs", "csharp", "public class Broken { void M( { ");
        Assert.Contains(extraction.Symbols, s => s.Name == "Broken");
        Assert.NotEmpty(extraction.Chunks);
    }
}

public class MarkdownExtractorTests
{
    [Fact]
    public void Headings_become_symbols_and_sections_become_chunks()
    {
        const string md = """
            # Guide

            Intro text.

            ## Setup

            Run `dotnet build`.

            ```bash
            # not a heading
            echo hi
            ```

            ### Details

            More.

            ## Usage

            Use it.
            """;

        var extraction = Extractor.Extract("README.md", "markdown", md);
        Assert.Equal(["Guide", "Guide > Setup", "Guide > Setup > Details", "Guide > Usage"], extraction.Symbols.Select(s => s.FullName));
        Assert.All(extraction.Symbols, s => Assert.Equal("heading", s.Kind));
        Assert.Equal("Guide > Setup", extraction.Symbols[2].Container);
        var setup = Assert.Single(extraction.Chunks, c => c.Heading == "Guide > Setup");
        Assert.Contains("dotnet build", setup.Text, StringComparison.Ordinal);
        Assert.Contains("echo hi", setup.Text, StringComparison.Ordinal);
    }
}

public class OtherExtractorTests
{
    [Fact]
    public void Typescript_and_python_declarations_are_found()
    {
        var ts = Extractor.Extract("a.ts", "typescript", "export async function fetchOrders(id: string) {\n  return 1;\n}\nexport class OrderClient {\n}\nconst MAX = 3;\n");
        Assert.Equal(["fetchOrders", "OrderClient", "MAX"], ts.Symbols.Select(s => s.Name));
        Assert.Equal("function", ts.Symbols[0].Kind);
        Assert.Equal(3, ts.Symbols[0].EndLine);

        var py = Extractor.Extract("a.py", "python", "class Order:\n    def total(self):\n        return 1\n\ndef main():\n    pass\n");
        Assert.Equal(["Order", "total", "main"], py.Symbols.Select(s => s.Name));
        Assert.Equal(4, py.Symbols[0].EndLine);
    }

    [Fact]
    public void Plain_text_is_windowed_with_overlap()
    {
        var text = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"line {i}"));
        var extraction = Extractor.Extract("notes.txt", "text", text);
        Assert.Empty(extraction.Symbols);
        Assert.True(extraction.Chunks.Count >= 3);
        Assert.Equal(1, extraction.Chunks[0].StartLine);
        Assert.Equal(Extractor.MaxChunkLines, extraction.Chunks[0].EndLine);
        Assert.Equal(Extractor.MaxChunkLines + 1 - Extractor.ChunkOverlap, extraction.Chunks[1].StartLine);
        Assert.Equal(200, extraction.Chunks[^1].EndLine);
    }

    [Fact]
    public void Secrets_in_chunks_are_redacted()
    {
        var extraction = Extractor.Extract("appsettings.json", "json", "{ \"ConnectionStrings\": { \"Db\": \"Host=db;Password=hunter22;Database=x\" } }");
        Assert.DoesNotContain("hunter22", extraction.Chunks[0].Text, StringComparison.Ordinal);
    }
}

public class IgnoreRulesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcp-ignore-" + Guid.NewGuid().ToString("N"));

    public IgnoreRulesTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, IgnoreRules.FileName), "# comment\ngenerated/\n*.g.cs\n!keep.g.cs\n/docs/private.md\n");
    }

    [Theory]
    [InlineData("src/App/bin/Debug/App.dll", true)]
    [InlineData("node_modules/x/index.js", true)]
    [InlineData(".env", true)]
    [InlineData("config/.env.production", true)]
    [InlineData("certs/server.pem", true)]
    [InlineData("assets/logo.png", true)]
    [InlineData("package-lock.json", true)]
    [InlineData("generated/Model.cs", true)]
    [InlineData("src/generated/Model.cs", true)]
    [InlineData("src/Foo.g.cs", true)]
    [InlineData("src/keep.g.cs", false)]
    [InlineData("docs/private.md", true)]
    [InlineData("other/docs/private.md", false)]
    [InlineData("src/App/Program.cs", false)]
    [InlineData("README.md", false)]
    public void Applies_fixed_secret_and_user_rules(string path, bool ignored)
    {
        var rules = IgnoreRules.Load(_root, includeGitignore: false);
        Assert.Equal(ignored, rules.IsIgnored(path));
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}

public class LanguageTests
{
    [Theory]
    [InlineData("src/A.cs", "csharp")]
    [InlineData("web/app.tsx", "typescript")]
    [InlineData("README.md", "markdown")]
    [InlineData("Dockerfile", "dockerfile")]
    [InlineData("build/x.csproj", "msbuild")]
    [InlineData("unknown.zzz", "text")]
    public void Detects_language(string path, string expected)
    {
        Assert.Equal(expected, Language.Detect(path));
    }

    [Fact]
    public void Detects_binary_by_nul_byte()
    {
        Assert.True(Language.IsBinary([0x4D, 0x5A, 0x00, 0x01]));
        Assert.False(Language.IsBinary("hello"u8));
    }
}
