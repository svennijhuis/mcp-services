using System.Text;
using System.Text.RegularExpressions;

namespace McpServices.Learnings;

public enum Outcome
{
    Worked,
    Failed,
    Partial,
}

public enum ProposalStatus
{
    Draft,
    Dispatched,
    PrOpened,
    Merged,
    Rejected,
    Failed,
}

/// <summary>Input for a new learning; free text is redacted before storage.</summary>
public sealed record NewLearning(
    Outcome Outcome,
    string Title,
    string? Detail = null,
    string? Category = null,
    IReadOnlyList<string>? Tags = null,
    string? Repo = null,
    IReadOnlyList<string>? Files = null,
    string? ToolOrSkill = null,
    string? Provider = null,
    string? Model = null,
    string? Evidence = null,
    string Source = "mcp",
    string? Meta = null);

public sealed record Learning(
    long Id,
    string Fingerprint,
    Outcome Outcome,
    string Category,
    string Title,
    string Detail,
    IReadOnlyList<string> Tags,
    string? Repo,
    IReadOnlyList<string> Files,
    string? ToolOrSkill,
    string? Provider,
    string? Model,
    string? Evidence,
    string Source,
    string Meta,
    int Occurrences,
    int UsefulCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset LastSeenAt);

public sealed record LearningFilter(
    string? Repo = null,
    Outcome? Outcome = null,
    string? Category = null,
    string? Tag = null,
    string? ToolOrSkill = null,
    DateTimeOffset? Since = null);

public sealed record FeedbackRow(long Id, string Tool, bool Worked, string? Note, string? Repo, string? Provider, DateTimeOffset CreatedAt);

public sealed record Proposal(
    long Id,
    ProposalStatus Status,
    string TargetRepo,
    string Title,
    string Rationale,
    IReadOnlyList<long> LearningIds,
    IReadOnlyList<string> LikelyFiles,
    IReadOnlyList<string> AcceptanceCriteria,
    string Prompt,
    int Corroborations,
    string? DispatchMode,
    string? ExternalId,
    string? PrUrl,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record DispatchRow(long Id, long ProposalId, string Mode, string RequestId, string? ExternalId, string Status, string? Detail, DateTimeOffset CreatedAt);

/// <summary>Repository references in learnings come in many shapes; this normalizes them to <c>owner/name</c> when possible.</summary>
public static partial class RepoRef
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var match = GitHubUrl().Match(trimmed);
        if (match.Success)
        {
            return $"{match.Groups[1].Value}/{match.Groups[2].Value}".ToLowerInvariant();
        }

        if (OwnerName().IsMatch(trimmed))
        {
            return trimmed.ToLowerInvariant();
        }

        // A local path: use the directory name so learnings from different clones still group.
        return Path.GetFileName(Path.TrimEndingDirectorySeparator(trimmed)).ToLowerInvariant();
    }

    public static string ToUrl(string normalized) =>
        normalized.Contains("://", StringComparison.Ordinal) ? normalized : $"https://github.com/{normalized}";

    public static bool Matches(string? a, string? b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    [GeneratedRegex(@"^(?:https?://|git@)?(?:www\.)?github\.com[:/]([\w.-]+)/([\w.-]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubUrl();

    [GeneratedRegex(@"^[\w.-]+/[\w.-]+$")]
    private static partial Regex OwnerName();
}

public static partial class Text
{
    public static string NormalizeTitle(string title) =>
        NonWord().Replace(title.ToLowerInvariant(), " ").Trim();

    public static IReadOnlyList<string> NormalizeTags(IEnumerable<string>? tags) =>
        (tags ?? []).Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    public static IReadOnlyList<string> NormalizeFiles(IEnumerable<string>? files) =>
        (files ?? []).Select(f => f.Trim().Replace('\\', '/').TrimStart('/')).Where(f => f.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    public static string Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max ? text ?? string.Empty : text[..max] + "…";

    public static string Join(IEnumerable<string> values, string empty = "None")
    {
        var sb = new StringBuilder();
        foreach (var value in values)
        {
            if (sb.Length > 0)
            {
                sb.Append(", ");
            }

            sb.Append(value);
        }

        return sb.Length == 0 ? empty : sb.ToString();
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();
}
