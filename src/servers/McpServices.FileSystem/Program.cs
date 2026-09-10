using McpServices.FileSystem;
using McpServices.Hosting;
using Microsoft.Extensions.DependencyInjection;

var descriptor = new ServerDescriptor("mcp-filesystem", "Sandboxed file system access: read, write, edit with diff preview, search and directory trees inside allowed directories.")
{
    Instructions = "All paths must be inside the allowed directories (see list_allowed_directories). Prefer edit_file over write_file for changes to existing files; use dryRun to preview.",
    Usage = """
        Usage: mcp-filesystem [transport options] <allowed-dir> [<allowed-dir> ...]

        Allowed directories can also be provided via MCP_FS_ALLOWED_DIRS (path-separator delimited).
        """,
};

return await McpServerHost.RunAsync(args, descriptor, context =>
{
    var dirs = context.Args.Positionals.ToList();
    if (dirs.Count == 0)
    {
        var env = Environment.GetEnvironmentVariable("MCP_FS_ALLOWED_DIRS");
        if (!string.IsNullOrWhiteSpace(env))
        {
            dirs.AddRange(env.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
    }

    var guard = new PathGuard(dirs);
    context.Expose("allowedDirectories", guard.AllowedDirectories);
    context.Services.AddSingleton(guard);
    context.Mcp.WithTools<FileSystemTools>(ToolJson.Options);
});
