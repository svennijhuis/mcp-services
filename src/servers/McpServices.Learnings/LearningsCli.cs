using McpServices.Hosting;
using McpServices.Learnings.Proposals;
using McpServices.Storage;
using Microsoft.Extensions.Logging;

namespace McpServices.Learnings;

/// <summary>
/// Non-MCP entry points for clients that never speak MCP (git hooks, CI, shell aliases):
/// <c>mcp-learnings record|import|export|recommend|stats</c>. Shares the store with a running server.
/// </summary>
public static class LearningsCli
{
    private static readonly string[] Commands = ["record", "import", "export", "recommend", "stats"];

    public static bool IsCliInvocation(string[] args) =>
        args.Length > 0 && Commands.Contains(args[0], StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args)
    {
        var commandLine = CommandLine.Parse(args, "quiet", "worked", "failed", "partial");
        var command = commandLine.Positionals[0].ToLowerInvariant();
        var quiet = commandLine.HasFlag("quiet");

        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(quiet ? LogLevel.Warning : LogLevel.Information)
            .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace));

        try
        {
            var options = BuildOptions(commandLine);
            var definition = commandLine.GetOption("store", "MCP_LEARNINGS_STORE");
            var storeOptions = string.IsNullOrWhiteSpace(definition) ? StoreOptions.DefaultSqlite("mcp-learnings", "learnings.db") : StoreOptions.Parse(definition);
            await using var store = KnowledgeStoreFactory.Create(storeOptions, loggerFactory);
            await store.InitializeAsync(LearningsSchema.Migrations).ConfigureAwait(false);
            var repository = new LearningsRepository(store);
            await using var connection = await store.OpenAsync().ConfigureAwait(false);

            object output;
            switch (command)
            {
                case "record":
                    {
                        var outcome = commandLine.HasFlag("worked") ? Outcome.Worked : commandLine.HasFlag("failed") ? Outcome.Failed : commandLine.HasFlag("partial") ? Outcome.Partial
                            : ToolGuard.OneOf(commandLine.GetOption("outcome"), "outcome", Outcome.Partial);
                        var title = commandLine.GetOption("title") ?? (commandLine.Positionals.Count > 1 ? commandLine.Positionals[1] : throw new ServerStartupException("record needs a title: mcp-learnings record --worked \"title\" [--detail ...]"));
                        var (learning, created) = await repository.RecordAsync(connection, new NewLearning(
                            outcome,
                            title,
                            commandLine.GetOption("detail"),
                            commandLine.GetOption("category"),
                            commandLine.GetOptions("tag"),
                            commandLine.GetOption("repo") ?? options.DefaultRepo,
                            commandLine.GetOptions("file"),
                            commandLine.GetOption("tool"),
                            commandLine.GetOption("provider"),
                            commandLine.GetOption("model"),
                            commandLine.GetOption("evidence"),
                            "cli"), CancellationToken.None).ConfigureAwait(false);
                        output = new { created, learning };
                        break;
                    }

                case "import":
                    {
                        var path = Positional(commandLine, 1, "import needs a path: mcp-learnings import docs/learnings.md");
                        var markdown = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                        var entries = LearningsMarkdown.Parse(markdown, commandLine.GetOption("repo") ?? options.DefaultRepo);
                        var created = 0;
                        foreach (var entry in entries)
                        {
                            created += (await repository.RecordAsync(connection, entry, CancellationToken.None).ConfigureAwait(false)).Created ? 1 : 0;
                        }

                        output = new { path, parsed = entries.Count, created, merged = entries.Count - created };
                        break;
                    }

                case "export":
                    {
                        var path = Positional(commandLine, 1, "export needs a path: mcp-learnings export docs/learnings.md");
                        var filter = new LearningFilter(commandLine.GetOption("repo") ?? options.DefaultRepo, null, commandLine.GetOption("category"), null, null, LearningsTools.ParseSince(commandLine.GetOption("since")));
                        var learnings = await repository.QueryAsync(connection, null, filter, 1000, CancellationToken.None).ConfigureAwait(false);
                        var appended = await LearningsMarkdown.AppendAsync(path, learnings.OrderBy(l => l.LastSeenAt).Select(LearningsMarkdown.Format), CancellationToken.None).ConfigureAwait(false);
                        output = new { path, candidates = learnings.Count, appended };
                        break;
                    }

                case "recommend":
                    {
                        var task = commandLine.Positionals.Count > 1 ? commandLine.Positionals[1] : null;
                        var service = new RecommendationService(repository, options);
                        output = await service.RecommendAsync(connection, commandLine.GetOption("repo") ?? options.DefaultRepo, task, commandLine.GetOptions("tag"), commandLine.GetInt("limit", 8), CancellationToken.None).ConfigureAwait(false);
                        break;
                    }

                case "stats":
                    {
                        var filter = new LearningFilter(commandLine.GetOption("repo") ?? options.DefaultRepo);
                        var total = await repository.CountAsync(connection, filter, CancellationToken.None).ConfigureAwait(false);
                        var worked = await repository.CountAsync(connection, filter with { Outcome = Outcome.Worked }, CancellationToken.None).ConfigureAwait(false);
                        var failed = await repository.CountAsync(connection, filter with { Outcome = Outcome.Failed }, CancellationToken.None).ConfigureAwait(false);
                        var proposals = await repository.ListProposalsAsync(connection, null, 1000, CancellationToken.None).ConfigureAwait(false);
                        output = new { store = store.Location, total, worked, failed, partial = total - worked - failed, proposals = proposals.GroupBy(p => p.Status).ToDictionary(g => LearningsRepository.StatusName(g.Key), g => g.Count()) };
                        break;
                    }

                default:
                    throw new ServerStartupException($"Unknown command '{command}'.");
            }

            Console.Out.WriteLine(ToolJson.Serialize(output));
            return 0;
        }
        catch (Exception ex) when (ex is ServerStartupException or ToolException or IOException)
        {
            Console.Error.WriteLine("mcp-learnings: " + ex.Message);
            return 2;
        }
    }

    private static string Positional(CommandLine commandLine, int index, string error) =>
        commandLine.Positionals.Count > index ? Path.GetFullPath(commandLine.Positionals[index]) : throw new ServerStartupException(error);

    public static LearningsOptions BuildOptions(CommandLine args)
    {
        var policy = (args.GetOption("dispatch", "MCP_LEARNINGS_DISPATCH") ?? "manual").ToLowerInvariant() switch
        {
            "off" or "none" => DispatchPolicy.Off,
            "manual" => DispatchPolicy.Manual,
            "auto" => DispatchPolicy.Auto,
            var other => throw new ServerStartupException($"Unknown --dispatch '{other}'. Use off, manual or auto."),
        };

        var dataDir = args.GetOption("data-dir", "MCP_LEARNINGS_DATA_DIR");
        var proposalsDir = args.GetOption("proposals-dir") ?? Path.Combine(dataDir ?? Path.Combine(StoreOptions.DataHome, "mcp-learnings"), "proposals");
        var apiBase = args.GetOption("cursor-api", "MCP_LEARNINGS_CURSOR_API") ?? "https://api.cursor.com";
        var webhook = args.GetOption("webhook", "MCP_LEARNINGS_WEBHOOK_URL");
        var targets = args.GetOptions("target-repo").Concat((Environment.GetEnvironmentVariable("MCP_LEARNINGS_TARGET_REPOS") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        var halfLifeDays = args.GetInt("decay-half-life-days", 90);

        return new LearningsOptions
        {
            DefaultRepo = RepoRef.Normalize(args.GetOption("repo", "MCP_LEARNINGS_REPO")),
            Dispatch = policy,
            DefaultDispatchMode = args.GetOption("dispatch-mode", "MCP_LEARNINGS_DISPATCH_MODE") ?? "dry-run",
            MinCorroborations = Math.Max(1, args.GetInt("min-corroborations", 3)),
            MaxDispatchesPerDay = Math.Max(0, args.GetInt("max-dispatches-per-day", 2)),
            TargetRepos = targets,
            ProposalsDirectory = Path.GetFullPath(proposalsDir),
            CursorApiBase = Uri.TryCreate(apiBase, UriKind.Absolute, out var uri) ? uri : throw new ServerStartupException($"Invalid --cursor-api '{apiBase}'."),
            CursorModel = args.GetOption("cursor-model", "MCP_LEARNINGS_CURSOR_MODEL"),
            BaseRef = args.GetOption("base-ref", "MCP_LEARNINGS_BASE_REF") ?? "main",
            CursorCliCommand = args.GetOption("cursor-cli", "MCP_LEARNINGS_CURSOR_CLI") ?? "agent",
            WebhookUrl = webhook is null ? null : Uri.TryCreate(webhook, UriKind.Absolute, out var hook) ? hook : throw new ServerStartupException($"Invalid --webhook '{webhook}'."),
            DecayHalfLife = TimeSpan.FromDays(Math.Max(1, halfLifeDays)),
        };
    }
}
