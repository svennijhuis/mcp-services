using System.ComponentModel;
using System.Globalization;
using McpServices.Hosting;
using McpServices.Learnings.Proposals;
using McpServices.Storage;
using ModelContextProtocol.Server;

namespace McpServices.Learnings;

[McpServerToolType]
public sealed class LearningsTools(StoreInitializer initializer, LearningsRepository repository, RecommendationService recommendations, ProposalService proposals, DispatchService dispatch, LearningsOptions options)
{
    private const string RepoDescription = "Repository the learning belongs to: owner/name, a GitHub URL or a local path. Defaults to the server's --repo. Omit for learnings that apply everywhere.";

    // Recording

    [McpServerTool(Name = "record_learning", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, Title = "Record learning")]
    [Description("Record what worked or failed during a task. Repeats of the same learning (same outcome, title, repo, files and tool) increase its occurrence count instead of duplicating it. Secrets are redacted. Call this at the end of a task, once per distinct insight.")]
    public async Task<object> RecordLearning(
        [Description("worked, failed or partial.")] string outcome,
        [Description("One-line statement of the learning, e.g. 'Run dotnet format before committing to avoid CI style failures'.")] string title,
        [Description("What happened, why it worked/failed, and what to do instead. Plain text, a few sentences.")] string? detail = null,
        [Description("Free category: tooling, workflow, testing, prompt, code, docs, review, squad, ... (default general).")] string? category = null,
        [Description("Tags for later filtering, e.g. ['dotnet','ci'].")] string[]? tags = null,
        [Description(RepoDescription)] string? repo = null,
        [Description("Repository-relative files the learning is about.")] string[]? files = null,
        [Description("Tool, MCP server, skill or command the learning concerns (e.g. 'mcp-index', '/squad', 'dotnet test').")] string? toolOrSkill = null,
        [Description("Client that produced the learning: cursor, claude, codex, copilot, grok, ...")] string? provider = null,
        [Description("Model name if known.")] string? model = null,
        [Description("Short evidence: an error message, a test name, a PR link.")] string? evidence = null,
        CancellationToken cancellationToken = default)
    {
        var parsed = ToolGuard.OneOf(outcome, "outcome", Outcome.Partial);
        var store = await initializer.StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var (learning, created) = await repository.RecordAsync(connection, new NewLearning(parsed, title, detail, category, tags, repo ?? options.DefaultRepo, files, toolOrSkill, provider, model, evidence), cancellationToken).ConfigureAwait(false);
        return new { created, learning, note = created ? "New learning recorded." : $"Matched an existing learning; occurrences is now {learning.Occurrences}." };
    }

    [McpServerTool(Name = "record_feedback", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, Title = "Record tool feedback")]
    [Description("Lightweight thumbs up/down for a tool, MCP server or skill you just used. Aggregated by summarize_period; use record_learning for anything with a lesson attached.")]
    public async Task<object> RecordFeedback(
        [Description("Tool, server, skill or command name.")] string tool,
        [Description("True if it did what you needed.")] bool worked,
        [Description("Optional short note (what was wrong or good).")] string? note = null,
        [Description(RepoDescription)] string? repo = null,
        [Description("Client that produced the feedback.")] string? provider = null,
        CancellationToken cancellationToken = default)
    {
        ToolGuard.NotEmpty(tool, "tool");
        var store = await initializer.StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = await repository.RecordFeedbackAsync(connection, tool, worked, note, repo ?? options.DefaultRepo, provider, cancellationToken).ConfigureAwait(false);
        return new { id, tool = tool.Trim(), worked };
    }

    [McpServerTool(Name = "mark_learning_useful", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, Title = "Mark learnings useful")]
    [Description("Tell the server which recommendations actually helped; boosts their confidence for future get_recommendations calls.")]
    public async Task<object> MarkLearningUseful(
        [Description("Learning ids from get_recommendations or query_learnings.")] long[] learningIds,
        CancellationToken cancellationToken = default)
    {
        var store = await initializer.StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var updated = await repository.MarkUsefulAsync(connection, learningIds, cancellationToken).ConfigureAwait(false);
        return new { updated };
    }

    [McpServerTool(Name = "forget_learning", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, Title = "Delete learning")]
    [Description("Delete a learning that is wrong or obsolete. Proposals that referenced it keep their copy of the text.")]
    public async Task<object> ForgetLearning([Description("Learning id.")] long learningId, CancellationToken cancellationToken = default)
    {
        var store = await initializer.StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        return new { deleted = await repository.DeleteAsync(connection, learningId, cancellationToken).ConfigureAwait(false) };
    }

    // Using

    [McpServerTool(Name = "get_recommendations", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Get recommendations")]
    [Description("Before starting a task: returns a 'do / avoid / caution' list with confidence and evidence, based on earlier learnings for this repo (plus global ones). Conflicting learnings are flagged, most recent first.")]
    public async Task<RecommendationSet> GetRecommendations(
        [Description("What you are about to do, in a sentence.")] string? task = null,
        [Description(RepoDescription)] string? repo = null,
        [Description("Tags to focus on.")] string[]? tags = null,
        [Description("Max items per list (default 8, max 25).")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var store = await initializer.StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await recommendations.RecommendAsync(connection, repo ?? options.DefaultRepo, task, tags, Math.Clamp(limit ?? 8, 1, 25), cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "query_learnings", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Query learnings")]
    [Description("Full-text search over learnings with filters (repo, outcome, category, tag, tool, since). Without a query returns the most recently seen learnings.")]
    public async Task<object> QueryLearnings(
        [Description("Free-text query (optional).")] string? query = null,
        [Description(RepoDescription)] string? repo = null,
        [Description("worked, failed or partial.")] string? outcome = null,
        [Description("Category filter.")] string? category = null,
        [Description("Tag filter (single tag).")] string? tag = null,
        [Description("Tool/skill filter.")] string? toolOrSkill = null,
        [Description("Only learnings seen since this ISO-8601 timestamp or a relative duration like '7d', '24h'.")] string? since = null,
        [Description("Max results (default 20, max 200).")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var filter = new LearningFilter(repo ?? options.DefaultRepo, ParseOutcome(outcome), category, tag, toolOrSkill, ParseSince(since));
        var store = await initializer.StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var results = await repository.QueryAsync(connection, query, filter, Math.Clamp(limit ?? 20, 1, 200), cancellationToken).ConfigureAwait(false);
        return new { total = results.Count, learnings = results };
    }

    [McpServerTool(Name = "summarize_period", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Summarize period")]
    [Description("Digest of a period: counts per outcome and category, most corroborated learnings, tools with the most negative feedback, and proposals created. Good input for a weekly review.")]
    public async Task<object> SummarizePeriod(
        [Description("Start of the period: ISO-8601 or relative ('7d', '30d'). Default 7d.")] string? since = null,
        [Description(RepoDescription)] string? repo = null,
        CancellationToken cancellationToken = default)
    {
        var from = ParseSince(since ?? "7d")!.Value;
        var filter = new LearningFilter(Repo: repo ?? options.DefaultRepo, Since: from);
        var store = await initializer.StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);

        var learnings = await repository.QueryAsync(connection, null, filter, 500, cancellationToken).ConfigureAwait(false);
        var feedback = await repository.FeedbackSummaryAsync(connection, from, repo ?? options.DefaultRepo, cancellationToken).ConfigureAwait(false);
        var proposalRows = (await repository.ListProposalsAsync(connection, null, 100, cancellationToken).ConfigureAwait(false)).Where(p => p.CreatedAt >= from).ToList();

        return new
        {
            since = from,
            repo = RepoRef.Normalize(repo ?? options.DefaultRepo),
            learnings = new
            {
                total = learnings.Count,
                byOutcome = learnings.GroupBy(l => l.Outcome).ToDictionary(g => g.Key.ToString().ToLowerInvariant(), g => g.Count()),
                byCategory = learnings.GroupBy(l => l.Category).OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count()),
                mostCorroborated = learnings.OrderByDescending(l => l.Occurrences + l.UsefulCount).Take(10).Select(l => new { l.Id, l.Outcome, l.Title, l.Occurrences, l.UsefulCount, l.ToolOrSkill, l.LastSeenAt }),
                readyForProposal = learnings.Where(l => l.Occurrences >= options.MinCorroborations).Select(l => l.Id).ToList(),
            },
            feedback = feedback.Select(f => new { f.Tool, f.Worked, f.Failed, f.LastAt }),
            proposals = proposalRows.Select(p => new { p.Id, p.Status, p.Title, p.TargetRepo, p.PrUrl }),
        };
    }

    // Import / export

    [McpServerTool(Name = "import_learnings_md", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, Title = "Import learnings.md")]
    [Description("Import a squad-style docs/learnings.md (append-only log with '## date — /squad' entries) or entries written by export_learnings_md. Re-importing is safe: existing entries only bump occurrence counts.")]
    public async Task<object> ImportLearningsMd(
        [Description("Path to the markdown file.")] string path,
        [Description(RepoDescription)] string? repo = null,
        CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(ToolGuard.NotEmpty(path, "path")));
        if (!File.Exists(full))
        {
            throw new ToolException($"File '{path}' does not exist.");
        }

        var markdown = await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
        var entries = LearningsMarkdown.Parse(markdown, repo ?? options.DefaultRepo ?? GuessRepo(full));
        var store = await initializer.StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var created = 0;
        var merged = 0;
        var ids = new List<long>();
        foreach (var entry in entries)
        {
            var (learning, isNew) = await repository.RecordAsync(connection, entry, cancellationToken).ConfigureAwait(false);
            ids.Add(learning.Id);
            if (isNew)
            {
                created++;
            }
            else
            {
                merged++;
            }
        }

        return new { path = full, parsed = entries.Count, created, merged, ids };
    }

    [McpServerTool(Name = "export_learnings_md", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, Title = "Export learnings.md")]
    [Description("Append learnings to a markdown log in the squad docs/learnings.md shape (squad entries round-trip exactly; other learnings use a '## date — learning' entry the squad parser ignores). Never rewrites existing entries.")]
    public async Task<object> ExportLearningsMd(
        [Description("Target markdown file (created with a '# Learnings' title if missing).")] string path,
        [Description(RepoDescription)] string? repo = null,
        [Description("Only learnings seen since this ISO-8601 timestamp or relative duration.")] string? since = null,
        [Description("Only this category (e.g. squad).")] string? category = null,
        CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(ToolGuard.NotEmpty(path, "path")));
        var store = await initializer.StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var learnings = await repository.QueryAsync(connection, null, new LearningFilter(repo ?? options.DefaultRepo, null, category, null, null, ParseSince(since)), 1000, cancellationToken).ConfigureAwait(false);
        var ordered = learnings.OrderBy(l => l.LastSeenAt).Select(LearningsMarkdown.Format).ToList();
        var appended = await LearningsMarkdown.AppendAsync(full, ordered, cancellationToken).ConfigureAwait(false);
        return new { path = full, candidates = learnings.Count, appended };
    }

    // Improving

    [McpServerTool(Name = "create_proposal", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, Title = "Create improvement proposal")]
    [Description("Aggregate learnings (by ids or by filter) into a structured improvement proposal: target repo, rationale, likely files, acceptance criteria and a ready-to-run agent prompt. With --dispatch auto and enough corroboration it is dispatched immediately; otherwise call dispatch_proposal.")]
    public async Task<object> CreateProposal(
        [Description("Learning ids to include.")] long[]? learningIds = null,
        [Description("Alternatively: full-text query selecting learnings.")] string? query = null,
        [Description("Filter: repository of the learnings.")] string? repo = null,
        [Description("Filter: worked, failed or partial.")] string? outcome = null,
        [Description("Filter: category.")] string? category = null,
        [Description("Filter: tag.")] string? tag = null,
        [Description("Repository that should receive the PR (owner/name). Must be whitelisted with --target-repo/--repo. Defaults to the learnings' repo.")] string? targetRepo = null,
        [Description("Title for the proposal/PR (generated when omitted).")] string? title = null,
        [Description("Custom acceptance criteria (defaults are sensible).")] string[]? acceptanceCriteria = null,
        CancellationToken cancellationToken = default)
    {
        var store = await initializer.StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var filter = new LearningFilter(repo ?? options.DefaultRepo, ParseOutcome(outcome), category, tag);
        var proposal = await proposals.CreateAsync(connection, learningIds, query, filter, targetRepo, title, acceptanceCriteria, cancellationToken).ConfigureAwait(false);

        DispatchOutcome? dispatched = null;
        string? autoNote = null;
        if (options.Dispatch == DispatchPolicy.Auto)
        {
            var dispatcher = dispatch.Resolve(null);
            var blocked = await dispatch.BlockedReasonAsync(connection, proposal, dispatcher, cancellationToken).ConfigureAwait(false);
            if (blocked is null)
            {
                dispatched = await dispatch.DispatchAsync(connection, proposal.Id, null, cancellationToken).ConfigureAwait(false);
                proposal = dispatched.Proposal;
            }
            else
            {
                autoNote = "Not auto-dispatched: " + blocked;
            }
        }

        return new
        {
            proposal,
            dispatched = dispatched is null ? null : new { dispatched.Mode, dispatched.RequestId, dispatched.Result },
            note = autoNote ?? (options.Dispatch == DispatchPolicy.Off ? "Dispatch is off; use dispatch_proposal with mode dry-run to write the prompt to a file." : $"Call dispatch_proposal(proposalId: {proposal.Id}, mode: '{options.DefaultDispatchMode}') to hand it to Cursor."),
        };
    }

    [McpServerTool(Name = "list_proposals", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "List proposals")]
    [Description("List improvement proposals, optionally by status (draft, dispatched, pr_opened, merged, rejected, failed).")]
    public async Task<object> ListProposals(
        [Description("Status filter.")] string? status = null,
        [Description("Max results (default 50).")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var store = await initializer.StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await repository.ListProposalsAsync(connection, string.IsNullOrWhiteSpace(status) ? null : LearningsRepository.ParseStatus(status), Math.Clamp(limit ?? 50, 1, 500), cancellationToken).ConfigureAwait(false);
        return new
        {
            total = rows.Count,
            proposals = rows.Select(p => new { p.Id, p.Status, p.Title, p.TargetRepo, p.Corroborations, p.LearningIds, p.DispatchMode, p.PrUrl, p.LastError, p.CreatedAt, p.UpdatedAt }),
            dispatch = new { policy = options.Dispatch, defaultMode = options.DefaultDispatchMode, modes = dispatch.Availability() },
        };
    }

    [McpServerTool(Name = "get_proposal", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Get proposal")]
    [Description("Full proposal including the generated agent prompt and its dispatch history.")]
    public async Task<object> GetProposal([Description("Proposal id.")] long proposalId, CancellationToken cancellationToken = default)
    {
        var store = await initializer.StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var proposal = await repository.GetProposalAsync(connection, proposalId, cancellationToken).ConfigureAwait(false) ?? throw new ToolException($"Proposal #{proposalId} does not exist.");
        var history = await repository.ListDispatchesAsync(connection, proposalId, cancellationToken).ConfigureAwait(false);
        return new { proposal, history, markdown = ImprovementPrompt.ToMarkdown(proposal) };
    }

    [McpServerTool(Name = "dispatch_proposal", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true, Title = "Dispatch proposal")]
    [Description("Hand a proposal to something that opens a draft PR. Modes: dry-run (write prompt to a file), cursor-cloud (Cursor Cloud Agents API, needs CURSOR_API_KEY), cursor-cli (local 'agent' + git + gh), webhook. Guardrails: --dispatch policy, minimum corroborations, daily cap, target-repo whitelist.")]
    public async Task<object> DispatchProposal(
        [Description("Proposal id.")] long proposalId,
        [Description("dry-run, cursor-cloud, cursor-cli or webhook (default from --dispatch-mode).")] string? mode = null,
        CancellationToken cancellationToken = default)
    {
        var store = await initializer.StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var outcome = await dispatch.DispatchAsync(connection, proposalId, mode, cancellationToken).ConfigureAwait(false);
        return new { outcome.Mode, outcome.RequestId, outcome.Result, proposal = outcome.Proposal };
    }

    [McpServerTool(Name = "get_dispatch_status", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true, Title = "Get dispatch status")]
    [Description("Current status of a dispatched proposal. For cursor-cloud it polls the Cloud Agent and records the PR URL once available.")]
    public async Task<object> GetDispatchStatus([Description("Proposal id.")] long proposalId, CancellationToken cancellationToken = default)
    {
        var store = await initializer.StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var (proposal, poll, history) = await dispatch.StatusAsync(connection, proposalId, cancellationToken).ConfigureAwait(false);
        return new { proposal.Id, proposal.Status, proposal.DispatchMode, proposal.ExternalId, proposal.PrUrl, proposal.LastError, remote = poll, history };
    }

    [McpServerTool(Name = "update_proposal_status", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, Title = "Update proposal status")]
    [Description("Manually set a proposal's status (e.g. merged or rejected after human review) and optionally its PR URL.")]
    public async Task<object> UpdateProposalStatus(
        [Description("Proposal id.")] long proposalId,
        [Description("draft, dispatched, pr_opened, merged, rejected or failed.")] string status,
        [Description("Pull request URL, if known.")] string? prUrl = null,
        [Description("Reason (stored as last error / note).")] string? note = null,
        CancellationToken cancellationToken = default)
    {
        var parsed = LearningsRepository.ParseStatus(ToolGuard.NotEmpty(status, "status"));
        var store = await initializer.StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var updated = await repository.UpdateProposalAsync(connection, proposalId, parsed, null, null, prUrl, note, cancellationToken).ConfigureAwait(false);
        if (updated == 0)
        {
            throw new ToolException($"Proposal #{proposalId} does not exist.");
        }

        return new { proposal = await repository.GetProposalAsync(connection, proposalId, cancellationToken).ConfigureAwait(false) };
    }

    // Helpers

    public static Outcome? ParseOutcome(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : ToolGuard.OneOf(value, "outcome", Outcome.Partial);

    public static DateTimeOffset? ParseSince(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > 1 && char.IsDigit(trimmed[0]) && "dhwm".Contains(char.ToLowerInvariant(trimmed[^1]), StringComparison.Ordinal) && int.TryParse(trimmed[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount))
        {
            return char.ToLowerInvariant(trimmed[^1]) switch
            {
                'h' => DateTimeOffset.UtcNow.AddHours(-amount),
                'd' => DateTimeOffset.UtcNow.AddDays(-amount),
                'w' => DateTimeOffset.UtcNow.AddDays(-7 * amount),
                _ => DateTimeOffset.UtcNow.AddMonths(-amount),
            };
        }

        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        throw new ToolException($"Cannot parse since '{value}'. Use ISO-8601 or a relative duration like 7d, 24h, 2w.");
    }

    private static string? GuessRepo(string markdownPath)
    {
        // docs/learnings.md lives in the repository root's docs folder; use the root's folder name.
        var directory = Path.GetDirectoryName(markdownPath);
        if (directory is not null && Path.GetFileName(directory).Equals("docs", StringComparison.OrdinalIgnoreCase))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory is null ? null : RepoRef.Normalize(directory);
    }
}

[McpServerPromptType]
public sealed class LearningsPrompts(StoreInitializer initializer, LearningsRepository repository, ProposalService proposals, LearningsOptions options)
{
    [McpServerPrompt(Name = "improvement_pr", Title = "Improvement PR prompt")]
    [Description("The prompt used to turn learnings into a draft pull request. Pass a proposalId to get the stored prompt, or learningIds to render one ad hoc.")]
    public async Task<string> ImprovementPr(
        [Description("Existing proposal id.")] long? proposalId = null,
        [Description("Learning ids (comma separated) to render a prompt without storing a proposal.")] string? learningIds = null,
        [Description("Target repository (owner/name).")] string? targetRepo = null,
        CancellationToken cancellationToken = default)
    {
        var store = await initializer.StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (proposalId is { } id)
        {
            var proposal = await repository.GetProposalAsync(connection, id, cancellationToken).ConfigureAwait(false) ?? throw new ToolException($"Proposal #{id} does not exist.");
            return proposal.Prompt;
        }

        var ids = (learningIds ?? string.Empty).Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => long.Parse(s.TrimStart('#'), CultureInfo.InvariantCulture)).ToList();
        var learnings = ids.Count == 0
            ? await repository.QueryAsync(connection, null, new LearningFilter(options.DefaultRepo), 10, cancellationToken).ConfigureAwait(false)
            : await repository.GetManyAsync(connection, ids, cancellationToken).ConfigureAwait(false);
        if (learnings.Count == 0)
        {
            throw new ToolException("No learnings to build a prompt from.");
        }

        var target = proposals.ResolveTarget(targetRepo, learnings);
        var title = "Apply corroborated agent learnings";
        var rationale = $"{learnings.Count} learning(s) selected for an improvement pass.";
        return ImprovementPrompt.Build(target, title, rationale, learnings, learnings.SelectMany(l => l.Files).Distinct(StringComparer.Ordinal).Take(12).ToList(), ImprovementPrompt.DefaultAcceptanceCriteria);
    }
}

[McpServerResourceType]
public sealed class LearningsResources(StoreInitializer initializer, LearningsRepository repository)
{
    [McpServerResource(UriTemplate = "learnings://proposals/{id}", Name = "proposal", MimeType = "text/markdown", Title = "Proposal")]
    [Description("A proposal rendered as markdown (status, evidence and the agent prompt).")]
    public async Task<string> Proposal(string id, CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(id, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new ToolException($"Invalid proposal id '{id}'.");
        }

        var store = await initializer.StoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var proposal = await repository.GetProposalAsync(connection, parsed, cancellationToken).ConfigureAwait(false) ?? throw new ToolException($"Proposal #{id} does not exist.");
        return ImprovementPrompt.ToMarkdown(proposal);
    }
}
