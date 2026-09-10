using System.Globalization;
using System.Text;
using McpServices.Storage;

namespace McpServices.Learnings.Proposals;

/// <summary>
/// The single template behind <c>create_proposal</c> and the <c>improvement_pr</c> MCP prompt. It asks
/// for the smallest change that captures corroborated learnings and always a draft PR reviewed by a
/// human; the server itself never edits skills or rules.
/// </summary>
public static class ImprovementPrompt
{
    public static readonly IReadOnlyList<string> DefaultAcceptanceCriteria =
    [
        "The change is the smallest edit that captures the learnings (docs, skills, rules, prompts or comments); no wholesale rewrites.",
        "The pull request description lists the learning ids and quotes the evidence for each change.",
        "Only files related to the learnings are touched; unrelated formatting is left alone.",
        "No secrets, tokens or connection strings appear in the diff.",
        "Existing tests and validators keep passing.",
        "The pull request is opened as a draft for human review.",
    ];

    public static string Build(string targetRepo, string title, string rationale, IReadOnlyList<Learning> learnings, IReadOnlyList<string> likelyFiles, IReadOnlyList<string> acceptanceCriteria)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"You are improving the repository {RepoRef.ToUrl(targetRepo)} based on learnings that coding agents recorded after real tasks. Several independent runs corroborate them.");
        sb.AppendLine();
        sb.AppendLine("## Goal");
        sb.AppendLine(title);
        sb.AppendLine();
        sb.AppendLine("## Why");
        sb.AppendLine(rationale);
        sb.AppendLine();
        sb.AppendLine("## Learnings (evidence)");
        foreach (var learning in learnings)
        {
            sb.Append(CultureInfo.InvariantCulture, $"- [#{learning.Id}, {learning.Outcome.ToString().ToLowerInvariant()} ×{learning.Occurrences}, last seen {learning.LastSeenAt:yyyy-MM-dd}] {learning.Title}");
            if (learning.Detail.Length > 0)
            {
                sb.Append(" — ").Append(Text.Truncate(learning.Detail.Replace('\n', ' '), 500));
            }

            var context = new List<string>();
            if (learning.ToolOrSkill is { Length: > 0 })
            {
                context.Add("tool/skill: " + learning.ToolOrSkill);
            }

            if (learning.Files.Count > 0)
            {
                context.Add("files: " + string.Join(", ", learning.Files.Take(6)));
            }

            if (learning.Provider is { Length: > 0 })
            {
                context.Add("provider: " + learning.Provider);
            }

            if (context.Count > 0)
            {
                sb.Append(" (").Append(string.Join("; ", context)).Append(')');
            }

            sb.AppendLine();
            if (learning.Evidence is { Length: > 0 })
            {
                sb.Append("  Evidence: ").AppendLine(Text.Truncate(learning.Evidence.Replace('\n', ' '), 300));
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Likely files");
        if (likelyFiles.Count == 0)
        {
            sb.AppendLine("- Not known; locate the skill, rule or document that governs the behaviour above before editing.");
        }
        else
        {
            foreach (var file in likelyFiles)
            {
                sb.Append("- ").AppendLine(file);
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Constraints");
        sb.AppendLine("- Make the smallest change that captures the learnings; prefer adding a sentence, example or guard over rewriting a file.");
        sb.AppendLine("- Do not touch unrelated files. Never commit secrets. Keep the repository's own conventions and validators green.");
        sb.AppendLine("- If a learning contradicts the current guidance, explain the conflict in the PR description instead of silently overriding it.");
        sb.AppendLine("- Do not modify existing entries in any docs/learnings.md; that log is append-only.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Open the result as a DRAFT pull request titled \"{title}\" and list the learning ids above in its description.");
        sb.AppendLine();
        sb.AppendLine("## Acceptance criteria");
        foreach (var criterion in acceptanceCriteria)
        {
            sb.Append("- ").AppendLine(criterion);
        }

        return SecretRedactor.Redact(sb.ToString().TrimEnd())!;
    }

    /// <summary>Human-readable file for dry runs and the <c>learnings://proposals/{id}</c> resource.</summary>
    public static string ToMarkdown(Proposal proposal)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Proposal #{proposal.Id}: {proposal.Title}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Status: {LearningsRepository.StatusName(proposal.Status)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Target repo: {proposal.TargetRepo}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Corroborations: {proposal.Corroborations}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Learnings: {string.Join(", ", proposal.LearningIds.Select(id => "#" + id))}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Created: {proposal.CreatedAt:u}");
        if (proposal.DispatchMode is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Dispatch: {proposal.DispatchMode} {proposal.ExternalId}");
        }

        if (proposal.PrUrl is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Pull request: {proposal.PrUrl}");
        }

        sb.AppendLine();
        sb.AppendLine("## Agent prompt");
        sb.AppendLine();
        sb.AppendLine(proposal.Prompt);
        return sb.ToString();
    }
}
