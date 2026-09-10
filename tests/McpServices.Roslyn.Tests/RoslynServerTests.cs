using System.Text.Json;

namespace McpServices.Roslyn.Tests;

[Collection(SampleSolutionTests.Name)]
public sealed class RoslynServerTests(SampleSolutionFixture fixture)
{
    private readonly SampleSolutionFixture _fixture = fixture;

    [Fact]
    public async Task Server_exposes_the_expected_tool_set()
    {
        var tools = await _fixture.Server.ToolNamesAsync();
        string[] expected =
        [
            "list_workspaces", "load_solution", "load_project", "workspace_status", "unload_workspace", "list_projects", "get_project_info", "list_source_files", "list_namespaces",
            "find_symbols", "get_file_symbols", "get_type_members", "get_symbol_info", "go_to_definition", "find_references", "find_implementations", "find_callers", "get_call_graph", "get_type_hierarchy", "get_symbols_in_scope",
            "get_diagnostics", "compile_check", "find_unused_symbols", "get_complexity_metrics", "get_namespace_dependencies", "get_nuget_dependencies",
            "rename_symbol", "format_document", "organize_usings", "list_code_fixes", "apply_code_fix",
            "analyze_snippet", "get_syntax_tree", "run_script", "build_project", "test_run", "server_info",
        ];
        foreach (var name in expected)
        {
            Assert.Contains(name, tools);
        }
    }

    [Fact]
    public async Task List_workspaces_finds_the_sample_solution_and_msbuild_is_registered()
    {
        var result = await _fixture.Server.CallJsonAsync("list_workspaces");
        Assert.Equal("ok", result.GetProperty("msbuild").GetString());
        var candidates = result.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("path").GetString()).ToList();
        Assert.Contains(_fixture.SolutionPath, candidates);
    }

    [Fact]
    public async Task Load_solution_reports_two_csharp_projects()
    {
        var result = await _fixture.Server.CallJsonAsync("load_solution", new { path = _fixture.SolutionPath });
        Assert.Equal("solution", result.GetProperty("kind").GetString());
        var projects = result.GetProperty("projects").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Equal(["Sample.App", "Sample.Lib"], projects);
        Assert.False(string.IsNullOrEmpty(result.GetProperty("workspaceId").GetString()));

        var status = await _fixture.Server.CallJsonAsync("workspace_status");
        Assert.Equal(result.GetProperty("workspaceId").GetString(), status.GetProperty("workspaceId").GetString());
    }

    [Fact]
    public async Task Compile_check_succeeds_for_the_sample_solution()
    {
        await EnsureLoadedAsync();
        var result = await _fixture.Server.CallJsonAsync("compile_check");
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        Assert.Equal(2, result.GetProperty("projects").GetArrayLength());
    }

    [Fact]
    public async Task Find_symbols_matches_by_prefix_and_kind()
    {
        await EnsureLoadedAsync();
        var result = await _fixture.Server.CallJsonAsync("find_symbols", new { query = "Calc", matchMode = "prefix", kind = "class" });
        var names = result.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("fullName").GetString()).ToList();
        Assert.Contains("Sample.Lib.Calculator", names);
        Assert.DoesNotContain("Sample.Lib.ICalculator", names);

        var exact = await _fixture.Server.CallJsonAsync("find_symbols", new { query = "Add", matchMode = "exact" });
        Assert.Contains(exact.GetProperty("items").EnumerateArray(), i => i.GetProperty("fullName").GetString() == "Sample.Lib.Calculator.Add(int, int)");
    }

    [Fact]
    public async Task Get_symbol_info_includes_documentation_and_source()
    {
        await EnsureLoadedAsync();
        var result = await _fixture.Server.CallJsonAsync("get_symbol_info", new { symbol = "Sample.Lib.ICalculator.Add", includeSource = true });
        Assert.Equal("method", result.GetProperty("kind").GetString());
        Assert.Contains("Adds two numbers", result.GetProperty("documentation").GetString());
        Assert.Contains("int Add(int a, int b)", result.GetProperty("source").GetString());
        Assert.Equal(2, result.GetProperty("method").GetProperty("parameters").GetArrayLength());

        var byPosition = await _fixture.Server.CallJsonAsync("get_symbol_info", new { file = _fixture.OrderServiceFile, line = 18, column = 16 });
        Assert.Equal("Sample.Lib.Orders.OrderService.Total(Sample.Lib.Orders.Order)", byPosition.GetProperty("fullName").GetString());
    }

    [Fact]
    public async Task Find_references_and_callers_cross_project_boundaries()
    {
        await EnsureLoadedAsync();
        var references = await _fixture.Server.CallJsonAsync("find_references", new { symbol = "Sample.Lib.Orders.OrderService.Place" });
        var files = references.GetProperty("references").EnumerateArray().Select(r => Path.GetFileName(r.GetProperty("file").GetString())).ToList();
        Assert.Contains("Program.cs", files);
        Assert.Equal(2, files.Count(f => f == "Program.cs"));

        var callers = await _fixture.Server.CallJsonAsync("find_callers", new { symbol = "Sample.Lib.Orders.OrderService.Total" });
        var names = callers.GetProperty("callers").EnumerateArray().Select(c => c.GetProperty("caller").GetString()).ToList();
        Assert.Contains("Sample.Lib.Orders.OrderService.Place(Sample.Lib.Orders.Order)", names);
        Assert.Contains("Sample.Lib.Orders.OrderService.GrandTotal()", names);
    }

    [Fact]
    public async Task Find_implementations_and_type_hierarchy()
    {
        await EnsureLoadedAsync();
        var impls = await _fixture.Server.CallJsonAsync("find_implementations", new { symbol = "Sample.Lib.ICalculator" });
        var names = impls.GetProperty("implementations").EnumerateArray().Select(i => i.GetProperty("fullName").GetString()).ToList();
        Assert.Contains("Sample.Lib.Calculator", names);
        Assert.Contains("Sample.Lib.ScientificCalculator", names);

        var overrides = await _fixture.Server.CallJsonAsync("find_implementations", new { symbol = "Sample.Lib.Calculator.Describe" });
        Assert.Equal("overrides", overrides.GetProperty("relation").GetString());
        Assert.Contains(overrides.GetProperty("implementations").EnumerateArray(), i => i.GetProperty("fullName").GetString()!.StartsWith("Sample.Lib.ScientificCalculator.Describe", StringComparison.Ordinal));

        var hierarchy = await _fixture.Server.CallJsonAsync("get_type_hierarchy", new { type = "Sample.Lib.Calculator" });
        Assert.Contains(hierarchy.GetProperty("interfaces").EnumerateArray(), i => i.GetProperty("name").GetString() == "Sample.Lib.ICalculator");
        Assert.Contains(hierarchy.GetProperty("derived").EnumerateArray(), d => d.GetProperty("fullName").GetString() == "Sample.Lib.ScientificCalculator");
    }

    [Fact]
    public async Task Call_graph_walks_callers_and_callees()
    {
        await EnsureLoadedAsync();
        var graph = await _fixture.Server.CallJsonAsync("get_call_graph", new { symbol = "Sample.Lib.Orders.OrderService.Total", depth = 2 });
        var edges = graph.GetProperty("edges").EnumerateArray().Select(e => (From: e.GetProperty("from").GetString(), To: e.GetProperty("to").GetString())).ToList();
        Assert.Contains(edges, e => e.From == "Sample.Lib.Orders.OrderService.Place(Sample.Lib.Orders.Order)" && e.To == "Sample.Lib.Orders.OrderService.Total(Sample.Lib.Orders.Order)");
        Assert.Contains(edges, e => e.From == "Sample.Lib.Orders.OrderService.Total(Sample.Lib.Orders.Order)" && e.To == "Sample.Lib.ICalculator.Multiply(int, int)");
    }

    [Fact]
    public async Task File_symbols_and_type_members_outline_a_file()
    {
        await EnsureLoadedAsync();
        var outline = await _fixture.Server.CallJsonAsync("get_file_symbols", new { file = _fixture.CalculatorFile });
        var kinds = outline.GetProperty("symbols").EnumerateArray().Select(s => (Name: s.GetProperty("name").GetString(), Kind: s.GetProperty("kind").GetString())).ToList();
        Assert.Contains(("ICalculator", "interface"), kinds);
        Assert.Contains(("Calculator", "class"), kinds);
        Assert.Contains(("Describe", "method"), kinds);
        Assert.Contains(("_calls", "field"), kinds);

        var members = await _fixture.Server.CallJsonAsync("get_type_members", new { type = "ScientificCalculator", includeInherited = true });
        var inherited = members.GetProperty("members").EnumerateArray().Where(m => m.TryGetProperty("declaredIn", out var d) && d.ValueKind == JsonValueKind.String).Select(m => m.GetProperty("name").GetString()).ToList();
        Assert.Contains("Add", inherited);
    }

    [Fact]
    public async Task Symbols_in_scope_lists_locals_and_members()
    {
        await EnsureLoadedAsync();
        var scope = await _fixture.Server.CallJsonAsync("get_symbols_in_scope", new { file = _fixture.OrderServiceFile, line = 28, column = 13 });
        var names = scope.GetProperty("symbols").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToList();
        Assert.Contains("sum", names);
        Assert.Contains("order", names);
        Assert.Contains("_calculator", names);
        Assert.Equal("Sample.Lib.Orders.OrderService.GrandTotal()", scope.GetProperty("enclosingMember").GetString());
    }

    [Fact]
    public async Task Diagnostics_report_hidden_unnecessary_usings_when_asked()
    {
        await EnsureLoadedAsync();
        var warnings = await _fixture.Server.CallJsonAsync("get_diagnostics", new { scope = "file", file = _fixture.CalculatorFile });
        Assert.Equal(0, warnings.GetProperty("summary").GetProperty("errors").GetInt32());

        var fixes = await _fixture.Server.CallJsonAsync("list_code_fixes", new { file = _fixture.CalculatorFile, diagnosticId = "CS8019" });
        Assert.True(fixes.GetProperty("diagnosticsChecked").GetInt32() >= 1, fixes.ToString());
    }

    [Fact]
    public async Task Unused_symbols_and_complexity_metrics()
    {
        await EnsureLoadedAsync();
        var unused = await _fixture.Server.CallJsonAsync("find_unused_symbols", new { project = "Sample.Lib" });
        Assert.Contains(unused.GetProperty("unused").EnumerateArray(), u => u.GetProperty("fullName").GetString() == "Sample.Lib.Calculator.NeverCalled()");

        var metrics = await _fixture.Server.CallJsonAsync("get_complexity_metrics", new { file = _fixture.CalculatorFile });
        var describe = metrics.GetProperty("methods").EnumerateArray().First(m => m.GetProperty("name").GetString() == "Sample.Lib.Calculator.Describe(int)");
        Assert.True(describe.GetProperty("cyclomaticComplexity").GetInt32() >= 5, describe.ToString());
    }

    [Fact]
    public async Task Namespace_and_nuget_dependencies()
    {
        await EnsureLoadedAsync();
        var namespaces = await _fixture.Server.CallJsonAsync("get_namespace_dependencies");
        Assert.Contains(namespaces.GetProperty("dependencies").EnumerateArray(), d => d.GetProperty("from").GetString() == "Sample.Lib.Orders" && d.GetProperty("to").GetString() == "Sample.Lib");
        Assert.Equal(0, namespaces.GetProperty("cycles").GetArrayLength());

        var nuget = await _fixture.Server.CallJsonAsync("get_nuget_dependencies");
        Assert.Equal(2, nuget.GetProperty("projects").GetArrayLength());
        Assert.All(nuget.GetProperty("projects").EnumerateArray(), p => Assert.True(p.GetProperty("restored").GetBoolean()));
    }

    [Fact]
    public async Task Rename_dry_run_returns_diff_without_writing()
    {
        await EnsureLoadedAsync();
        var before = await File.ReadAllTextAsync(_fixture.OrderServiceFile);
        var result = await _fixture.Server.CallJsonAsync("rename_symbol", new { symbol = "Sample.Lib.Orders.OrderService.GrandTotal", newName = "SumOfOrders" });
        Assert.True(result.GetProperty("dryRun").GetBoolean());
        Assert.Equal(2, result.GetProperty("changedFiles").GetInt32());
        var diffs = result.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("diff").GetString()!).ToList();
        Assert.Contains(diffs, d => d.Contains("-    public int GrandTotal()", StringComparison.Ordinal) && d.Contains("+    public int SumOfOrders()", StringComparison.Ordinal));
        Assert.Equal(0, result.GetProperty("newErrors").GetArrayLength());
        Assert.Equal(before, await File.ReadAllTextAsync(_fixture.OrderServiceFile));
    }

    [Fact]
    public async Task New_file_on_disk_is_picked_up_and_organize_usings_applies_to_it()
    {
        await EnsureLoadedAsync();

        // A file created after loading is discovered by the per-call refresh, no reload needed.
        var scratch = Path.Combine(_fixture.Root, "src", "Sample.Lib", "Scratch.cs");
        await File.WriteAllTextAsync(scratch, "using System.Text.RegularExpressions;\nusing Sample.Lib.Orders;\nusing System.Text;\nusing System.Globalization;\n\nnamespace Sample.Lib.Scratch;\n\npublic static class Scratch\n{\n    public static bool Check(Order order) => Regex.IsMatch(order.Id, \"^a\") && order.Quantity.ToString(CultureInfo.InvariantCulture).Length > 0;\n}\n");

        var preview = await _fixture.Server.CallJsonAsync("organize_usings", new { file = scratch });
        Assert.True(preview.GetProperty("dryRun").GetBoolean());
        var diff = preview.GetProperty("changes").EnumerateArray().Single().GetProperty("diff").GetString()!;
        Assert.Contains("-using System.Text;", diff, StringComparison.Ordinal);

        var applied = await _fixture.Server.CallJsonAsync("organize_usings", new { file = scratch, dryRun = false });
        Assert.Single(applied.GetProperty("written").EnumerateArray());
        var text = await File.ReadAllTextAsync(scratch);
        Assert.StartsWith("using System.Globalization;\nusing System.Text.RegularExpressions;\nusing Sample.Lib.Orders;\n\nnamespace Sample.Lib.Scratch;", text, StringComparison.Ordinal);

        // Implicit usings make every directive here redundant; removing all of them must not leave a blank first line.
        var empty = Path.Combine(_fixture.Root, "src", "Sample.Lib", "Empty.cs");
        await File.WriteAllTextAsync(empty, "using System;\nusing System.Linq;\n\nnamespace Sample.Lib.Scratch;\n\npublic static class Empty\n{\n    public static int One() => new[] { 1 }.First();\n}\n");
        await _fixture.Server.CallJsonAsync("organize_usings", new { file = empty, dryRun = false });
        Assert.StartsWith("namespace Sample.Lib.Scratch;", await File.ReadAllTextAsync(empty), StringComparison.Ordinal);

        var compile = await _fixture.Server.CallJsonAsync("compile_check", new { project = "Sample.Lib" });
        Assert.True(compile.GetProperty("success").GetBoolean(), compile.ToString());
    }

    [Fact]
    public async Task Apply_code_fix_removes_unnecessary_usings_in_preview()
    {
        await EnsureLoadedAsync();
        var result = await _fixture.Server.CallJsonAsync("apply_code_fix", new { file = _fixture.CalculatorFile, diagnosticId = "CS8019", fixAll = true });
        Assert.True(result.GetProperty("dryRun").GetBoolean());
        Assert.True(result.GetProperty("appliedCount").GetInt32() >= 1, result.ToString());
        var diff = result.GetProperty("changes").EnumerateArray().Single().GetProperty("diff").GetString()!;
        Assert.Contains("-using System.Text;", diff, StringComparison.Ordinal);
        Assert.Contains("using System.Text;", await File.ReadAllTextAsync(_fixture.CalculatorFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Format_document_is_idempotent_on_formatted_code()
    {
        await EnsureLoadedAsync();
        var result = await _fixture.Server.CallJsonAsync("format_document", new { file = _fixture.ProgramFile });
        Assert.Equal(0, result.GetProperty("changedFiles").GetInt32());
    }

    [Fact]
    public async Task Analyze_snippet_reports_errors_and_symbols_without_a_workspace()
    {
        var ok = await _fixture.Server.CallJsonAsync("analyze_snippet", new { code = "public class Foo { public int Bar(int x) => x * 2; }", includeSyntaxTree = true });
        Assert.True(ok.GetProperty("compiles").GetBoolean(), ok.ToString());
        Assert.Contains(ok.GetProperty("symbols").EnumerateArray(), s => s.GetProperty("name").GetString() == "Bar");
        Assert.Equal("CompilationUnit", ok.GetProperty("syntaxTree").GetProperty("kind").GetString());

        var broken = await _fixture.Server.CallJsonAsync("analyze_snippet", new { code = "public class Foo { public int Bar() => \"nope\"; }" });
        Assert.False(broken.GetProperty("compiles").GetBoolean());
        Assert.Contains(broken.GetProperty("diagnostics").EnumerateArray(), d => d.GetProperty("id").GetString() == "CS0029");
    }

    [Fact]
    public async Task Run_script_returns_value_and_console_output()
    {
        var result = await _fixture.Server.CallJsonAsync("run_script", new { code = "Console.WriteLine(\"hello\"); var xs = Enumerable.Range(1, 4).ToList(); xs.Sum()" });
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        Assert.Equal(10, result.GetProperty("returnValue").GetInt32());
        Assert.Contains("hello", result.GetProperty("output").GetString());

        var failing = await _fixture.Server.CallJsonAsync("run_script", new { code = "int x = \"a\";" });
        Assert.False(failing.GetProperty("success").GetBoolean());
        Assert.Equal("compile", failing.GetProperty("stage").GetString());
    }

    [Fact]
    public async Task Syntax_tree_of_inline_code()
    {
        var tree = await _fixture.Server.CallJsonAsync("get_syntax_tree", new { code = "var x = 1 + 2;", maxDepth = 3 });
        Assert.Equal("CompilationUnit", tree.GetProperty("root").GetProperty("kind").GetString());
        Assert.Contains("GlobalStatement", tree.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restricted_root_rejects_paths_outside_it()
    {
        var error = await _fixture.Server.CallExpectingErrorAsync("load_solution", new { path = Path.GetTempPath() });
        Assert.Contains("outside the configured roots", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resources_and_prompt_are_available()
    {
        await EnsureLoadedAsync();
        var templates = await _fixture.Server.Client.ListResourceTemplatesAsync();
        Assert.Contains(templates, t => t.UriTemplate == "roslyn://{workspaceId}/diagnostics");
        var resources = await _fixture.Server.Client.ListResourcesAsync();
        Assert.Contains(resources, r => r.Uri == "roslyn://workspaces");

        var workspaces = await _fixture.Server.Client.ReadResourceAsync("roslyn://workspaces");
        var text = workspaces.Contents.OfType<ModelContextProtocol.Protocol.TextResourceContents>().Single().Text;
        Assert.Contains("SampleSolution.sln", text, StringComparison.Ordinal);

        var prompt = await _fixture.Server.Client.GetPromptAsync("explain_symbol", new Dictionary<string, object?> { ["symbol"] = "Sample.Lib.Orders.OrderService.Total" });
        var content = prompt.Messages.Single().Content;
        Assert.Contains("Sample.Lib.Orders.OrderService.Total", ((ModelContextProtocol.Protocol.TextContentBlock)content).Text, StringComparison.Ordinal);
    }

    private async Task EnsureLoadedAsync() => await _fixture.Server.CallJsonAsync("load_solution", new { path = _fixture.SolutionPath });
}
