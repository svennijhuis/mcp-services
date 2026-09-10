using McpServices.Hosting;
using McpServices.Index;
using McpServices.Index.Embeddings;
using McpServices.Index.Indexing;
using McpServices.Index.Search;
using McpServices.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var descriptor = new ServerDescriptor("mcp-index", "Learning codebase index: incremental symbol and full-text indexing, hybrid search with feedback, notes with staleness detection, and freshness tracking against git.")
{
    Instructions = "Start with index_status or search_code; the index refreshes itself when it is stale. Use mark_useful after a search that helped and remember/recall for facts worth keeping between sessions.",
    Flags = ["restrict", "watch", "quiet", "force"],
    Usage = """
        Usage: mcp-index [transport options] [--root <dir>]... [--store sqlite:<path>|postgres:<conn>] [--auto-refresh inline|background|off]
               mcp-index index|status|verify|rebuild <dir> [--store ...] [--force] [--quiet]   (CLI mode, for git hooks and CI)

        Options:
          --root <dir>              Repository root(s) tools may index (repeatable; also positional). Default: any directory passed to a tool.
          --restrict                Only allow repositories under the configured roots.
          --store <spec>            sqlite:<file> (default ~/.mcp-services/mcp-index/index.db) or postgres:<connection-string>; env MCP_INDEX_STORE.
          --auto-refresh <mode>     inline (default): refresh small deltas before answering; background: answer now, refresh later; off.
          --inline-max-files <n>    Largest delta refreshed inline (default 200).
          --max-file-kb <n>         Skip files larger than this (default 512).
          --embeddings <spec>       Optional embedding model for semantic search: ollama:<model>[@url] or openai:<model>[@url]
                                    (OPENAI_API_KEY / MCP_INDEX_EMBEDDINGS_URL from the environment); env MCP_INDEX_EMBEDDINGS.
          --watch                   Watch the configured roots and refresh in the background on file changes.
        """,
};

if (IndexCli.IsCliInvocation(args))
{
    return await IndexCli.RunAsync(args);
}

return await McpServerHost.RunAsync(args, descriptor, context =>
{
    var options = IndexCli.BuildOptions(context.Args);
    context.Expose("roots", options.Roots);
    context.Expose("autoRefresh", options.AutoRefresh.ToString().ToLowerInvariant());
    context.Expose("inlineMaxFiles", options.InlineMaxFiles);
    context.Expose("git", McpServices.Index.Git.GitCli.IsAvailable);
    context.Expose("embeddings", options.Embeddings ?? "off");
    context.Expose("watch", options.Watch);

    var store = KnowledgeStoreFactory.Resolve(context, "mcp-index", "index.db");
    context.AddKnowledgeStore(store, IndexSchema.Migrations);
    context.Services.AddSingleton(options);
    if (options.Embeddings is not null)
    {
        var (generator, fingerprint) = EmbeddingProviders.Create(options.Embeddings);
        context.Services.AddSingleton(sp => new EmbeddingService(
            sp.GetRequiredService<IKnowledgeStore>(),
            generator,
            fingerprint,
            sp.GetRequiredService<ILogger<EmbeddingService>>()));
    }

    if (options.Watch)
    {
        context.Services.AddHostedService<IndexWatcher>();
    }

    context.Services.AddSingleton<IndexRepository>();
    context.Services.AddSingleton<Indexer>();
    context.Services.AddSingleton<FreshnessChecker>();
    context.Services.AddSingleton<IndexCoordinator>();
    context.Services.AddSingleton<SearchService>();
    context.Services.AddSingleton<NotesService>();
    context.Services.AddSingleton<RelatedFilesService>();

    context.Mcp.WithTools<IndexTools>(ToolJson.Options);
    context.Mcp.WithResources<IndexResources>();
});
