namespace McpServices.Learnings;

public enum DispatchPolicy
{
    /// <summary>dispatch_proposal is disabled; dry-run files can still be written.</summary>
    Off,

    /// <summary>Default: an agent (or human) must call dispatch_proposal explicitly.</summary>
    Manual,

    /// <summary>create_proposal dispatches immediately once the guardrails pass.</summary>
    Auto,
}

/// <summary>Startup configuration of the learnings server; everything here is safe to show in server_info.</summary>
public sealed class LearningsOptions
{
    /// <summary>Repository used when a learning does not name one (e.g. "svennijhuis/agentPacks" or a URL).</summary>
    public string? DefaultRepo { get; init; }

    public DispatchPolicy Dispatch { get; init; } = DispatchPolicy.Manual;

    /// <summary>Default dispatch mode: dry-run, cursor-cloud, cursor-cli or webhook.</summary>
    public string DefaultDispatchMode { get; init; } = "dry-run";

    /// <summary>Independent learnings a proposal needs before it may be dispatched.</summary>
    public int MinCorroborations { get; init; } = 3;

    public int MaxDispatchesPerDay { get; init; } = 2;

    /// <summary>Repositories a proposal may target (owner/name or URL). Empty means only the default repo.</summary>
    public IReadOnlyList<string> TargetRepos { get; init; } = [];

    /// <summary>Directory for dry-run proposal files.</summary>
    public required string ProposalsDirectory { get; init; }

    public Uri CursorApiBase { get; init; } = new("https://api.cursor.com");

    /// <summary>Model passed to the Cloud Agents API; null lets Cursor choose.</summary>
    public string? CursorModel { get; init; }

    /// <summary>Base branch new proposals start from.</summary>
    public string BaseRef { get; init; } = "main";

    /// <summary>Executable used by the cursor-cli mode.</summary>
    public string CursorCliCommand { get; init; } = "agent";

    /// <summary>Optional webhook (Cursor Automation or anything else) for the webhook dispatch mode.</summary>
    public Uri? WebhookUrl { get; init; }

    /// <summary>Half-life used to decay confidence of old learnings.</summary>
    public TimeSpan DecayHalfLife { get; init; } = TimeSpan.FromDays(90);

    public bool HasCursorApiKey => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CURSOR_API_KEY"));

    public bool HasWebhookToken => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MCP_LEARNINGS_WEBHOOK_TOKEN"));
}
