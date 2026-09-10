namespace McpServices.Storage;

/// <summary>
/// Reciprocal Rank Fusion: merges several ranked lists (keyword hits, vector hits, feedback boosts)
/// into one ranking without needing comparable scores. Identical on SQLite and PostgreSQL because it
/// runs in-process.
/// </summary>
public static class Rrf
{
    public const int DefaultK = 60;

    public sealed record Fused<TKey>(TKey Key, double Score, IReadOnlyDictionary<string, int> Ranks);

    /// <summary>
    /// Fuses named rankings. Each ranking is an ordered list of keys (best first) with an optional
    /// weight; score = sum(weight / (k + rank)).
    /// </summary>
    public static List<Fused<TKey>> Fuse<TKey>(IEnumerable<(string Name, IReadOnlyList<TKey> Ranking, double Weight)> rankings, int k = DefaultK)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(rankings);
        var scores = new Dictionary<TKey, double>();
        var ranks = new Dictionary<TKey, Dictionary<string, int>>();

        foreach (var (name, ranking, weight) in rankings)
        {
            for (var i = 0; i < ranking.Count; i++)
            {
                var key = ranking[i];
                var rank = i + 1;
                scores[key] = scores.GetValueOrDefault(key) + weight / (k + rank);
                if (!ranks.TryGetValue(key, out var perSource))
                {
                    perSource = new Dictionary<string, int>(StringComparer.Ordinal);
                    ranks[key] = perSource;
                }

                if (!perSource.TryGetValue(name, out var existing) || rank < existing)
                {
                    perSource[name] = rank;
                }
            }
        }

        return scores
            .OrderByDescending(pair => pair.Value)
            .Select(pair => new Fused<TKey>(pair.Key, pair.Value, ranks[pair.Key]))
            .ToList();
    }

    public static List<Fused<TKey>> Fuse<TKey>(params (string Name, IReadOnlyList<TKey> Ranking)[] rankings)
        where TKey : notnull =>
        Fuse(rankings.Select(r => (r.Name, r.Ranking, 1.0)));
}
