using McpServices.Hosting;
using McpServices.Roslyn.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace McpServices.Roslyn;

public static class RoslynServer
{
    public static void Configure(HostContext context)
    {
        // MSBuild assemblies must be located before any MSBuildWorkspace type is JIT-compiled.
        WorkspaceManager.RegisterMsBuild();

        var options = RoslynOptions.From(context.Args);
        context.Expose("roots", options.Roots);
        context.Expose("restrict", options.RestrictToRoots);
        context.Expose("scripting", options.ScriptingEnabled);
        context.Expose("build", options.BuildEnabled);
        context.Expose("msbuild", WorkspaceManager.MsBuildRegistrationError ?? "ok");

        context.Services.AddSingleton(options);
        context.Services.AddSingleton<WorkspaceManager>();
        context.Services.AddSingleton<CodeFixCatalog>();

        context.Mcp.WithTools<WorkspaceTools>(ToolJson.Options);
        context.Mcp.WithTools<NavigationTools>(ToolJson.Options);
        context.Mcp.WithTools<DiagnosticsTools>(ToolJson.Options);
        context.Mcp.WithTools<RefactoringTools>(ToolJson.Options);
        context.Mcp.WithTools<SnippetTools>(ToolJson.Options);
        if (options.ScriptingEnabled)
        {
            context.Mcp.WithTools<ScriptingTools>(ToolJson.Options);
        }

        if (options.BuildEnabled)
        {
            context.Mcp.WithTools<BuildTools>(ToolJson.Options);
        }

        context.Mcp.WithResources<RoslynResources>();
        context.Mcp.WithPrompts<RoslynPrompts>();
    }
}
