using System.Data.Common;
using System.Globalization;
using System.Text;
using McpServices.Hosting;
using McpServices.Storage;

namespace McpServices.Learnings.Proposals;

/// <summary>Builds structured improvement proposals out of learnings and enforces the target-repo whitelist.</summary>
public sealed class ProposalService(LearningsRepository repository, LearningsOptions options)
{
    public IReadOnlyList<string> AllowedTargets =>
        options.TargetRepos.Concat(options.DefaultRepo is null ? [] : [options.DefaultRepo]).Select(r => RepoRef.Normalize(r)!).Distinct(StringComparer.Ordinal).ToList();

    public async Task<Proposal> CreateAsync(DbConnection connection, IReadOnlyList<long>? learningIds, string? query, LearningFilter filter, string? targetRepo, string? title, IReadOnlyList<string>? acceptanceCriteria, CancellationToken cancellationToken)
    {
        List<Learning> learnings;
        if (learningIds is { Count: > 0 })
        {
            learnings = await repository.GetManyAsync(connection, learningIds, cancellationToken).ConfigureAwait(false);
            if (learnings.Count == 0)
            {
                throw new ToolException("None of the given learningIds exist.");
            }
        }
        else
        {
            learnings = await repository.QueryAsync(connection, query, filter, 25, cancellationToken).ConfigureAwait(false);
            if (learnings.Count == 0)
            {
                throw new ToolException("No learnings match the filter; record some first or pass learningIds.");
            }
        }

        var target = ResolveTarget(targetRepo, learnings);
        var corroborations = learnings.Sum(l => l.Occurrences);
        var resolvedTitle = SecretRedactor.Redact(string.IsNullOrWhiteSpace(title) ? GenerateTitle(learnings) : title.Trim())!;
        var rationale = BuildRationale(learnings);
        var likelyFiles = learnings.SelectMany(l => l.Files).GroupBy(f => f, StringComparer.Ordinal).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(12).ToList();
        var criteria = acceptanceCriteria is { Count: > 0 } ? acceptanceCriteria.Select(c => SecretRedactor.Redact(c.Trim())!).Where(c => c.Length > 0).ToList() : ImprovementPrompt.DefaultAcceptanceCriteria;
        var prompt = ImprovementPrompt.Build(target, resolvedTitle, rationale, learnings, likelyFiles, criteria);

        var id = await repository.InsertProposalAsync(connection, target, resolvedTitle, rationale, learnings.Select(l => l.Id).ToList(), likelyFiles, criteria, prompt, corroborations, cancellationToken).ConfigureAwait(false);
        return (await repository.GetProposalAsync(connection, id, cancellationToken).ConfigureAwait(false))!;
    }

    public string ResolveTarget(string? requested, IReadOnlyList<Learning> learnings)
    {
        var allowed = AllowedTargets;
        var candidate = RepoRef.Normalize(requested)
            ?? learnings.Where(l => l.Repo is not null).GroupBy(l => l.Repo!, StringComparer.Ordinal).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()
            ?? RepoRef.Normalize(options.DefaultRepo)
            ?? throw new ToolException("No target repository: pass targetRepo, or start the server with --repo / --target-repo.");

        if (allowed.Count > 0 && !allowed.Contains(candidate, StringComparer.Ordinal))
        {
            throw new ToolException($"Target repository '{candidate}' is not whitelisted. Allowed: {string.Join(", ", allowed)} (configure with --target-repo).");
        }

        if (allowed.Count == 0)
        {
            throw new ToolException("No target repositories are configured. Start the server with --repo <owner/name> or --target-repo <owner/name> to allow proposals.");
        }

        return candidate;
    }

    private static string GenerateTitle(IReadOnlyList<Learning> learnings)
    {
        var top = learnings.OrderByDescending(l => l.Occurrences + l.UsefulCount).ThenByDescending(l => l.LastSeenAt).First();
        var category = learnings.GroupBy(l => l.Category, StringComparer.Ordinal).OrderByDescending(g => g.Count()).First().Key;
        var verb = top.Outcome == Outcome.Failed ? "Prevent" : "Adopt";
        return Text.Truncate($"{verb} ({category}): {top.Title}", 90);
    }

    private static string BuildRationale(IReadOnlyList<Learning> learnings)
    {
        var sb = new StringBuilder();
        var worked = learnings.Count(l => l.Outcome == Outcome.Worked);
        var failed = learnings.Count(l => l.Outcome == Outcome.Failed);
        sb.AppendLine(CultureInfo.InvariantCulture, $"{learnings.Count} learning(s) with {learnings.Sum(l => l.Occurrences)} recorded occurrences ({worked} worked, {failed} failed, {learnings.Count - worked - failed} partial) point at the same improvement.");
        foreach (var learning in learnings.OrderByDescending(l => l.Occurrences).Take(10))
        {
            sb.Append(CultureInfo.InvariantCulture, $"- {learning.Outcome.ToString().ToLowerInvariant()} ×{learning.Occurrences}: {learning.Title}");
            if (learning.ToolOrSkill is { Length: > 0 })
            {
                sb.Append(CultureInfo.InvariantCulture, $" [{learning.ToolOrSkill}]");
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
