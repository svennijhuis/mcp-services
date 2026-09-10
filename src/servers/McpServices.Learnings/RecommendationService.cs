using System.Data.Common;

namespace McpServices.Learnings;

public sealed record Recommendation(
    long Id,
    string Advice,
    string Title,
    string Detail,
    string? ToolOrSkill,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Files,
    int Occurrences,
    int UsefulCount,
    double Confidence,
    DateTimeOffset LastSeenAt,
    bool Conflict,
    string? Evidence);

public sealed record RecommendationSet(
    string? Repo,
    string? Task,
    IReadOnlyList<Recommendation> Do,
    IReadOnlyList<Recommendation> Avoid,
    IReadOnlyList<Recommendation> Caution,
    int Considered,
    string Note);

/// <summary>
/// Turns stored learnings into a short "do / avoid / caution" list for a task. Confidence grows with
/// corroboration (occurrences + useful votes) and decays with age; contradictory learnings (worked
/// and failed for the same thing) are both returned, flagged, most recent first.
/// </summary>
public sealed class RecommendationService(LearningsRepository repository, LearningsOptions options)
{
    public async Task<RecommendationSet> RecommendAsync(DbConnection connection, string? repo, string? task, IReadOnlyList<string>? tags, int limit, CancellationToken cancellationToken)
    {
        var normalizedTags = Text.NormalizeTags(tags);
        var query = string.Join(' ', new[] { task ?? string.Empty }.Concat(normalizedTags)).Trim();
        var filter = new LearningFilter(Repo: repo);

        var candidates = await repository.QueryAsync(connection, string.IsNullOrWhiteSpace(query) ? null : query, filter, limit * 4, cancellationToken).ConfigureAwait(false);
        if (candidates.Count < limit && !string.IsNullOrWhiteSpace(query))
        {
            // Fill up with the strongest general learnings for this repo so short queries still get context.
            var extra = await repository.QueryAsync(connection, null, filter, limit * 2, cancellationToken).ConfigureAwait(false);
            candidates = candidates.Concat(extra.Where(e => candidates.All(c => c.Id != e.Id))).ToList();
        }

        var now = DateTimeOffset.UtcNow;
        var groups = candidates.GroupBy(ConflictKey, StringComparer.Ordinal).ToList();
        var recommendations = new List<Recommendation>();
        foreach (var group in groups)
        {
            var outcomes = group.Select(g => g.Outcome).Distinct().ToList();
            var conflict = outcomes.Contains(Outcome.Worked) && outcomes.Contains(Outcome.Failed);
            foreach (var learning in group.OrderByDescending(g => g.LastSeenAt))
            {
                var repoMatch = repo is null || learning.Repo is not null;
                var confidence = Confidence(learning, now) * (repoMatch ? 1.0 : 0.8);
                recommendations.Add(new Recommendation(
                    learning.Id,
                    learning.Outcome switch { Outcome.Worked => "do", Outcome.Failed => "avoid", _ => "caution" },
                    learning.Title,
                    Text.Truncate(learning.Detail, 600),
                    learning.ToolOrSkill,
                    learning.Tags,
                    learning.Files,
                    learning.Occurrences,
                    learning.UsefulCount,
                    Math.Round(confidence, 3),
                    learning.LastSeenAt,
                    conflict,
                    Text.Truncate(learning.Evidence, 300) is { Length: > 0 } e ? e : null));
            }
        }

        // Conflicts first (the agent must know), then by confidence; preserve FTS order as a tie-breaker via stable sort.
        var ordered = recommendations
            .Select((r, i) => (r, i))
            .OrderByDescending(x => x.r.Conflict)
            .ThenByDescending(x => x.r.Confidence)
            .ThenBy(x => x.i)
            .Select(x => x.r)
            .ToList();

        return new RecommendationSet(
            RepoRef.Normalize(repo),
            task,
            ordered.Where(r => r.Advice == "do").Take(limit).ToList(),
            ordered.Where(r => r.Advice == "avoid").Take(limit).ToList(),
            ordered.Where(r => r.Advice == "caution").Take(limit).ToList(),
            candidates.Count,
            candidates.Count == 0
                ? "No learnings recorded yet for this repo/task. Call record_learning after the task so the next run benefits."
                : "Confidence combines corroboration (occurrences + useful votes) and recency. Conflicts show both sides, most recent first. Call mark_learning_useful for the ones that helped.");
    }

    /// <summary>1 - 1/(1 + n) for strength (n=1: 0.5, n=3: 0.75) times an age factor that never drops below 0.3.</summary>
    public double Confidence(Learning learning, DateTimeOffset now)
    {
        var corroboration = learning.Occurrences + learning.UsefulCount;
        var strength = 1.0 - 1.0 / (1.0 + corroboration);
        var ageDays = Math.Max(0, (now - learning.LastSeenAt).TotalDays);
        var decay = Math.Pow(0.5, ageDays / options.DecayHalfLife.TotalDays);
        return strength * (0.3 + 0.7 * decay);
    }

    private static string ConflictKey(Learning learning) =>
        Text.NormalizeTitle(learning.Title) + "|" + (learning.ToolOrSkill?.ToLowerInvariant() ?? string.Empty) + "|" + (learning.Repo ?? string.Empty);
}
