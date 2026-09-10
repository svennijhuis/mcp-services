using McpServices.Hosting;
using McpServices.Roslyn;

var descriptor = new ServerDescriptor("mcp-roslyn", "Roslyn-powered C#/.NET analysis: load solutions/projects, navigate symbols and references, diagnostics, metrics, refactorings with preview, snippets, scripting, build and test.")
{
    Instructions = "Start with list_workspaces or load_solution to get a workspaceId, then use find_symbols / get_symbol_info / find_references etc. All refactorings support dryRun=true and return a unified diff. Results are paginated with pageToken.",
    Flags = ["no-scripting", "no-build", "restrict"],
    Usage = """
        Usage: mcp-roslyn [transport options] [--root <dir>]... [--no-scripting] [--no-build] [--restrict]

        Options:
          --root <dir>       Directory (repeatable; also positional) searched by list_workspaces and allowed for loading. Default: current directory; env MCP_ROSLYN_ROOT.
          --restrict         Only allow loading solutions/projects under the roots.
          --no-scripting     Disable run_script (C# scripting executes code in the server process).
          --no-build         Disable build_project and test_run (they spawn dotnet build/test).
          --max-results <n>  Cap for navigation results before paging (default 2000).
        """,
};

return await McpServerHost.RunAsync(args, descriptor, context => RoslynServer.Configure(context));
