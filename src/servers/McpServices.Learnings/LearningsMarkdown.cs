using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using McpServices.Hosting;

namespace McpServices.Learnings;

/// <summary>One entry of the squad <c>docs/learnings.md</c> log (append-only, human readable).</summary>
public sealed record SquadEntry(
    string Date,
    string Entrypoint,
    string Provider,
    string ModelTier,
    string AgentsSpun,
    string Skipped,
    string Ran,
    string Result,
    string HandoffThemes,
    string NextTweak);

/// <summary>
/// Reads and writes the append-only <c>docs/learnings.md</c> format used by the agentPacks squad
/// plugin, byte-compatible with its <c>LearningsLog</c> parser, plus a sibling <c>## date — learning</c>
/// entry shape for generic learnings that the squad parser ignores by design.
/// </summary>
public static partial class LearningsMarkdown
{
    public const string Title = "# Learnings";

    private static readonly string[] SquadEntrypoints = ["squad", "squad-review", "build", "review"];

    public static IReadOnlyList<NewLearning> Parse(string markdown, string? repo)
    {
        var matches = Heading().Matches(markdown);
        var results = new List<NewLearning>();
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : markdown.Length;
            var block = markdown[start..end].TrimEnd();
            var date = matches[i].Groups[1].Value;
            var kind = matches[i].Groups[2].Value.Trim().Trim('/').ToLowerInvariant();

            if (SquadEntrypoints.Contains(kind, StringComparer.Ordinal))
            {
                results.Add(FromSquad(ParseSquad(block, date, kind), block, repo));
            }
            else if (kind == "learning")
            {
                results.Add(ParseGeneric(block, repo));
            }
        }

        return results;
    }

    public static SquadEntry ParseSquad(string block, string date, string headingEntrypoint)
    {
        var entrypoint = Canonical(Field(block, "Entrypoint") ?? headingEntrypoint);
        return new SquadEntry(
            date,
            entrypoint,
            (Field(block, "Provider") ?? string.Empty).ToLowerInvariant(),
            Field(block, "Model tier") ?? "inherit",
            Field(block, "Agents spun") ?? "None",
            Field(block, "Skipped") ?? "None",
            Field(block, "Ran") ?? "None",
            (Field(block, "Result") ?? string.Empty).ToLowerInvariant(),
            Field(block, "Handoff themes") ?? "None",
            Field(block, "Next tweak") ?? "None");
    }

    public static NewLearning FromSquad(SquadEntry entry, string? rawBlock, string? repo)
    {
        var outcome = entry.Result switch
        {
            "pass" => Outcome.Worked,
            "fail" or "failed" or "stopped" => Outcome.Failed,
            _ => Outcome.Partial,
        };
        var tweak = entry.NextTweak.Equals("None", StringComparison.OrdinalIgnoreCase) ? string.Empty : entry.NextTweak;
        var title = tweak.Length > 0 ? $"/{entry.Entrypoint} {entry.Result}: {tweak}" : $"/{entry.Entrypoint} {entry.Result} ({entry.Date})";
        var tags = new List<string> { "squad", entry.Entrypoint, entry.Result };
        if (entry.Provider.Length > 0)
        {
            tags.Add(entry.Provider);
        }

        tags.Add("tier:" + entry.ModelTier.ToLowerInvariant());
        return new NewLearning(
            outcome,
            title,
            rawBlock ?? Format(entry),
            "squad",
            tags,
            repo,
            [],
            entry.Entrypoint,
            entry.Provider.Length > 0 ? entry.Provider : null,
            null,
            entry.HandoffThemes.Equals("None", StringComparison.OrdinalIgnoreCase) ? null : entry.HandoffThemes,
            "import",
            JsonSerializer.Serialize(entry, ToolJson.Options));
    }

    public static string Format(SquadEntry entry) =>
        $"""
        ## {entry.Date} — /{entry.Entrypoint}

        - Entrypoint: {entry.Entrypoint}
        - Provider: {entry.Provider}
        - Model tier: {entry.ModelTier}
        - Agents spun: {entry.AgentsSpun}
        - Skipped: {entry.Skipped}
        - Ran: {entry.Ran}
        - Result: {entry.Result}
        - Handoff themes: {entry.HandoffThemes}
        - Next tweak: {entry.NextTweak}
        """;

    /// <summary>Renders a learning as a markdown entry: squad entries round-trip exactly, others use the generic shape.</summary>
    public static string Format(Learning learning)
    {
        if (learning.Category == "squad" && TryReadSquad(learning.Meta, out var entry))
        {
            return Format(entry);
        }

        var sb = new StringBuilder();
        sb.Append("## ").Append(learning.LastSeenAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).AppendLine(" — learning");
        sb.AppendLine();
        sb.Append("- Outcome: ").AppendLine(learning.Outcome.ToString().ToLowerInvariant());
        sb.Append("- Category: ").AppendLine(learning.Category);
        sb.Append("- Title: ").AppendLine(learning.Title);
        sb.Append("- Detail: ").AppendLine(OneLine(learning.Detail));
        if (learning.Repo is not null)
        {
            sb.Append("- Repo: ").AppendLine(learning.Repo);
        }

        sb.Append("- Tags: ").AppendLine(Text.Join(learning.Tags));
        sb.Append("- Files: ").AppendLine(Text.Join(learning.Files));
        sb.Append("- Tool: ").AppendLine(learning.ToolOrSkill ?? "None");
        sb.Append("- Provider: ").AppendLine(learning.Provider ?? "None");
        sb.Append("- Occurrences: ").AppendLine(learning.Occurrences.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(learning.Evidence))
        {
            sb.Append("- Evidence: ").AppendLine(OneLine(learning.Evidence));
        }

        return sb.ToString().TrimEnd();
    }

    public static bool TryReadSquad(string meta, out SquadEntry entry)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<SquadEntry>(meta, ToolJson.Options);
            if (parsed is not null && !string.IsNullOrEmpty(parsed.Date))
            {
                entry = parsed;
                return true;
            }
        }
        catch (JsonException)
        {
        }

        entry = null!;
        return false;
    }

    /// <summary>
    /// Appends entries that are not already present (matched on the exact entry text). Creates the
    /// file with the standard title when missing. Never rewrites earlier entries.
    /// </summary>
    public static async Task<int> AppendAsync(string path, IEnumerable<string> entries, CancellationToken cancellationToken)
    {
        var existing = File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : string.Empty;
        var normalizedExisting = existing.Replace("\r\n", "\n", StringComparison.Ordinal);
        var sb = new StringBuilder();
        if (normalizedExisting.Trim().Length == 0)
        {
            sb.Append(Title).Append('\n');
        }
        else if (!normalizedExisting.EndsWith('\n'))
        {
            sb.Append('\n');
        }

        var added = 0;
        foreach (var entry in entries)
        {
            var normalized = entry.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
            if (normalizedExisting.Contains(normalized, StringComparison.Ordinal) || sb.ToString().Contains(normalized, StringComparison.Ordinal))
            {
                continue;
            }

            sb.Append('\n').Append(normalized).Append('\n');
            added++;
        }

        if (added == 0 && existing.Length > 0)
        {
            return 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.AppendAllTextAsync(path, sb.ToString(), cancellationToken).ConfigureAwait(false);
        return added;
    }

    private static NewLearning ParseGeneric(string block, string? repo)
    {
        var outcome = Enum.TryParse<Outcome>(Field(block, "Outcome"), ignoreCase: true, out var parsed) ? parsed : Outcome.Partial;
        var title = Field(block, "Title") ?? "Untitled learning";
        return new NewLearning(
            outcome,
            title,
            Field(block, "Detail"),
            Field(block, "Category"),
            List(Field(block, "Tags")),
            Field(block, "Repo") ?? repo,
            List(Field(block, "Files")),
            NoneToNull(Field(block, "Tool")),
            NoneToNull(Field(block, "Provider")),
            null,
            Field(block, "Evidence"),
            "import");
    }

    private static string? Field(string block, string name)
    {
        var match = Regex.Match(block, @"^-\s+" + Regex.Escape(name) + @":\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static IReadOnlyList<string> List(string? value) =>
        NoneToNull(value) is { } text ? text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];

    private static string? NoneToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Equals("None", StringComparison.OrdinalIgnoreCase) ? null : value;

    private static string OneLine(string text) => text.Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', ' ').Trim();

    private static string Canonical(string value) => value.Trim().Trim('/').ToLowerInvariant() switch
    {
        "build" => "squad",
        "review" => "squad-review",
        var other => other,
    };

    [GeneratedRegex(@"^##\s+(\d{4}-\d{2}-\d{2})\s+[—-]+\s+(\S+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex Heading();
}
