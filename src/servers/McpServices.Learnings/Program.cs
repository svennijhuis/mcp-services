using McpServices.Hosting;
using McpServices.Learnings;
using McpServices.Learnings.Proposals;
using McpServices.Storage;
using Microsoft.Extensions.DependencyInjection;

var descriptor = new ServerDescriptor("mcp-learnings", "Self-learning loop: record what worked and what failed, get recommendations before the next task, and turn corroborated learnings into improvement proposals dispatched to Cursor as draft pull requests.")
{
    Instructions = "Call get_recommendations before starting work in a repository and record_learning (worked/failed/partial) when you finish. Learnings repeat across sessions; when several agree, create_proposal + dispatch_proposal produce a draft PR for a human to review. The server never edits skills or rules itself.",
    Usage = """
        Usage: mcp-learnings [transport options] [--repo <owner/name>] [--store sqlite:<path>|postgres:<conn>] [--dispatch off|manual|auto] [--dispatch-mode dry-run|cursor-cloud|cursor-cli|webhook]
               mcp-learnings record --worked|--failed|--partial "<title>" [--detail ..] [--tag ..]... [--tool ..] [--repo ..]   (CLI mode)
               mcp-learnings import <docs/learnings.md> | export <path> [--since 30d] | recommend "<task>" | stats

        Options:
          --repo <owner/name>            Default repository for learnings and proposals; env MCP_LEARNINGS_REPO.
          --target-repo <owner/name>     Additional repositories proposals may target (repeatable); env MCP_LEARNINGS_TARGET_REPOS (comma separated).
          --store <spec>                 sqlite:<file> (default ~/.mcp-services/mcp-learnings/learnings.db) or postgres:<connection-string>; env MCP_LEARNINGS_STORE.
          --data-dir <dir>               Directory for dry-run proposal files (default ~/.mcp-services/mcp-learnings).
          --dispatch <policy>            off | manual (default) | auto — whether proposals may be handed to Cursor.
          --dispatch-mode <mode>         Default mode for dispatch_proposal (default dry-run).
          --min-corroborations <n>       Learning occurrences required before an external dispatch (default 3).
          --max-dispatches-per-day <n>   Cap on external dispatches per rolling day (default 2).
          --base-ref <branch>            Branch proposals start from (default main).
          --cursor-api <url>             Cloud Agents API base (default https://api.cursor.com); key from env CURSOR_API_KEY only.
          --cursor-model <model>         Model for cloud agents (default: let Cursor choose).
          --cursor-cli <command>         Cursor CLI executable for cursor-cli mode (default agent).
          --webhook <url>                Webhook for webhook mode; bearer token from env MCP_LEARNINGS_WEBHOOK_TOKEN.
          --decay-half-life-days <n>     Confidence half-life (default 90).
        """,
};

if (LearningsCli.IsCliInvocation(args))
{
    return await LearningsCli.RunAsync(args);
}

return await McpServerHost.RunAsync(args, descriptor, context =>
{
    var options = LearningsCli.BuildOptions(context.Args);
    context.Expose("repo", options.DefaultRepo);
    context.Expose("targetRepos", options.TargetRepos);
    context.Expose("dispatch", new { policy = options.Dispatch.ToString().ToLowerInvariant(), defaultMode = options.DefaultDispatchMode, options.MinCorroborations, options.MaxDispatchesPerDay, cursorApiKey = options.HasCursorApiKey ? "set" : "missing" });
    context.Expose("proposalsDirectory", options.ProposalsDirectory);

    var store = KnowledgeStoreFactory.Resolve(context, "mcp-learnings", "learnings.db");
    context.AddKnowledgeStore(store, LearningsSchema.Migrations);
    context.Services.AddSingleton(options);
    context.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(60) });
    context.Services.AddSingleton<LearningsRepository>();
    context.Services.AddSingleton<RecommendationService>();
    context.Services.AddSingleton<ProposalService>();
    context.Services.AddSingleton<IProposalDispatcher, DryRunDispatcher>();
    context.Services.AddSingleton<IProposalDispatcher, CursorCloudDispatcher>();
    context.Services.AddSingleton<IProposalDispatcher, CursorCliDispatcher>();
    context.Services.AddSingleton<IProposalDispatcher, WebhookDispatcher>();
    context.Services.AddSingleton<DispatchService>();

    context.Mcp.WithTools<LearningsTools>(ToolJson.Options);
    context.Mcp.WithPrompts<LearningsPrompts>();
    context.Mcp.WithResources<LearningsResources>();
});
