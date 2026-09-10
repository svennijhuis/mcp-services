using McpServices.Hosting;
using McpServices.Index.Indexing;
using McpServices.Storage;
using Microsoft.Extensions.Logging;

namespace McpServices.Index;

/// <summary>
/// Non-MCP entry points so the index stays warm when nothing talks MCP: git hooks
/// (<c>post-checkout</c>, <c>post-merge</c>, <c>post-commit</c>), CI, or a manual run. Shares the
/// store and writer lock with a running server, so both can coexist.
/// </summary>
public static class IndexCli
{
    private static readonly string[] Commands = ["index", "status", "verify", "rebuild"];

    public static bool IsCliInvocation(string[] args) =>
        args.Length > 0 && Commands.Contains(args[0], StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args)
    {
        var commandLine = CommandLine.Parse(args, "force", "quiet", "restrict", "watch", "repair");
        var command = commandLine.Positionals[0].ToLowerInvariant();
        var root = commandLine.Positionals.Count > 1 ? commandLine.Positionals[1] : Directory.GetCurrentDirectory();
        var quiet = commandLine.HasFlag("quiet");

        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(quiet ? LogLevel.Warning : LogLevel.Information)
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
            .AddFilter((_, level) => level >= (quiet ? LogLevel.Warning : LogLevel.Information)));

        try
        {
            if (!Directory.Exists(root))
            {
                throw new ServerStartupException($"Directory '{root}' does not exist.");
            }

            var definition = commandLine.GetOption("store", "MCP_INDEX_STORE");
            var storeOptions = string.IsNullOrWhiteSpace(definition) ? StoreOptions.DefaultSqlite("mcp-index", "index.db") : StoreOptions.Parse(definition);
            await using var store = KnowledgeStoreFactory.Create(storeOptions, loggerFactory);
            await store.InitializeAsync(IndexSchema.Migrations).ConfigureAwait(false);

            var options = BuildOptions(commandLine);
            var repository = new IndexRepository(store);
            var indexer = new Indexer(store, repository, options, loggerFactory.CreateLogger<Indexer>());
            var freshness = new FreshnessChecker(store, repository, options);
            var identity = RepoIdentity.FromPath(root);

            object output;
            switch (command)
            {
                case "index":
                    output = await indexer.RunAsync(identity, commandLine.HasFlag("force"), null, CancellationToken.None).ConfigureAwait(false);
                    break;
                case "rebuild":
                    output = await indexer.RunAsync(identity, force: true, null, CancellationToken.None).ConfigureAwait(false);
                    break;
                case "status":
                    output = await freshness.CheckAsync(identity, CancellationToken.None).ConfigureAwait(false);
                    break;
                case "verify":
                    {
                        await using var connection = await store.OpenAsync().ConfigureAwait(false);
                        var files = await repository.LoadFilesAsync(connection, identity.RepoId, CancellationToken.None).ConfigureAwait(false);
                        var mismatched = new List<string>();
                        foreach (var file in files.Values)
                        {
                            var full = Path.Combine(identity.Root, file.Path);
                            if (!File.Exists(full) || RepoIdentity.ContentHash(await File.ReadAllBytesAsync(full).ConfigureAwait(false)) != file.ContentHash)
                            {
                                mismatched.Add(file.Path);
                            }
                        }

                        var orphans = await repository.CountOrphansAsync(connection, CancellationToken.None).ConfigureAwait(false);
                        var removed = commandLine.HasFlag("repair") ? await repository.CollectOrphansAsync(connection, CancellationToken.None).ConfigureAwait(false) : 0;
                        output = new { repoId = identity.RepoId, files = files.Count, mismatched, orphanedContents = orphans, orphansRemoved = removed, healthy = mismatched.Count == 0 && orphans == 0 };
                        break;
                    }

                default:
                    throw new ServerStartupException($"Unknown command '{command}'.");
            }

            Console.Out.WriteLine(ToolJson.Serialize(output));
            return 0;
        }
        catch (ServerStartupException ex)
        {
            Console.Error.WriteLine("mcp-index: " + ex.Message);
            return 2;
        }
    }

    public static IndexOptions BuildOptions(CommandLine args)
    {
        var roots = args.GetOptions("root").Concat(args.Positionals.Where(p => !Commands.Contains(p, StringComparer.OrdinalIgnoreCase))).ToList();
        if (roots.Count == 0 && Environment.GetEnvironmentVariable("MCP_INDEX_ROOT") is { Length: > 0 } envRoot)
        {
            roots.Add(envRoot);
        }

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                throw new ServerStartupException($"Root '{root}' does not exist.");
            }
        }

        var refresh = (args.GetOption("auto-refresh", "MCP_INDEX_AUTO_REFRESH") ?? "inline").ToLowerInvariant() switch
        {
            "inline" => AutoRefreshMode.Inline,
            "background" => AutoRefreshMode.Background,
            "off" or "none" => AutoRefreshMode.Off,
            var other => throw new ServerStartupException($"Unknown --auto-refresh '{other}'. Use inline, background or off."),
        };

        return new IndexOptions
        {
            Roots = roots.Select(Path.GetFullPath).ToList(),
            RestrictToRoots = args.HasFlag("restrict"),
            AutoRefresh = refresh,
            InlineMaxFiles = args.GetInt("inline-max-files", 200),
            MaxFileKb = args.GetInt("max-file-kb", 512),
            FreshnessMaxFiles = args.GetInt("freshness-max-files", 5000),
            Embeddings = args.GetOption("embeddings", "MCP_INDEX_EMBEDDINGS"),
            Watch = args.HasFlag("watch"),
        };
    }
}
