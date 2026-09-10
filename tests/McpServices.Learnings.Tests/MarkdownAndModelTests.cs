using McpServices.Learnings;

namespace McpServices.Learnings.Tests;

public class LearningsMarkdownTests
{
    private const string SquadLog = """
        # Learnings

        ## 2026-09-05 — /squad

        - Entrypoint: squad
        - Provider: cursor
        - Model tier: inherit
        - Agents spun: squad-planner, squad-implementer, squad-verifier, squad-reviewer, squad-simplifier, squad-orchestrator
        - Skipped: squad-security-reviewer — no trust boundary changed
        - Ran: plan, implement, verify, dual-axis review, merge
        - Result: pass
        - Handoff themes: keep inherit; small rename skipped planner successfully
        - Next tweak: keep inherit; the small rename did not need a planner

        ## 2026-09-06 — /squad-review

        - Entrypoint: squad-review
        - Provider: claude
        - Model tier: standard
        - Agents spun: squad-reviewer
        - Skipped: None
        - Ran: dual-axis review
        - Result: fail
        - Handoff themes: None
        - Next tweak: demote to fast; the review found nothing the linter had not
        """;

    [Fact]
    public void Parses_squad_entries_into_learnings()
    {
        var learnings = LearningsMarkdown.Parse(SquadLog, "svennijhuis/agentPacks");

        Assert.Equal(2, learnings.Count);
        Assert.Equal(Outcome.Worked, learnings[0].Outcome);
        Assert.Equal("squad", learnings[0].ToolOrSkill);
        Assert.Equal("cursor", learnings[0].Provider);
        Assert.Contains("keep inherit", learnings[0].Title, StringComparison.Ordinal);
        Assert.Equal("svennijhuis/agentPacks", learnings[0].Repo);
        Assert.Contains("tier:inherit", learnings[0].Tags!);

        Assert.Equal(Outcome.Failed, learnings[1].Outcome);
        Assert.Equal("squad-review", learnings[1].ToolOrSkill);
        Assert.Equal("import", learnings[1].Source);
    }

    [Fact]
    public void Squad_entry_round_trips_exactly()
    {
        var learnings = LearningsMarkdown.Parse(SquadLog, null);
        Assert.True(LearningsMarkdown.TryReadSquad(learnings[0].Meta!, out var entry));

        var formatted = LearningsMarkdown.Format(entry);
        var original = SquadLog.Split("## 2026-09-06")[0].Split("## 2026-09-05")[1];
        Assert.Equal(("## 2026-09-05" + original).Trim(), formatted.Trim());
    }

    [Fact]
    public void Retired_build_and_review_headings_map_to_current_entrypoints()
    {
        var learnings = LearningsMarkdown.Parse("## 2026-01-01 — /build\n\n- Result: stopped\n- Next tweak: None\n\n## 2026-01-02 — /review\n\n- Result: pass\n", null);
        Assert.Equal("squad", learnings[0].ToolOrSkill);
        Assert.Equal(Outcome.Failed, learnings[0].Outcome);
        Assert.Equal("squad-review", learnings[1].ToolOrSkill);
    }

    [Fact]
    public void Generic_entries_round_trip()
    {
        var learning = new Learning(1, "fp", Outcome.Failed, "tooling", "dotnet test hangs without --no-build", "Use --no-build after a build step.", ["dotnet", "ci"], "svennijhuis/mcp-services", ["scripts/test.sh"], "dotnet test", "cursor", null, "Timeout after 10 min", "mcp", "{}", 3, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero));

        var markdown = LearningsMarkdown.Format(learning);
        Assert.StartsWith("## 2026-09-10 — learning", markdown, StringComparison.Ordinal);

        var parsed = LearningsMarkdown.Parse(markdown, null);
        var single = Assert.Single(parsed);
        Assert.Equal(Outcome.Failed, single.Outcome);
        Assert.Equal("dotnet test hangs without --no-build", single.Title);
        Assert.Equal("tooling", single.Category);
        Assert.Equal(["dotnet", "ci"], single.Tags!);
        Assert.Equal(["scripts/test.sh"], single.Files!);
        Assert.Equal("dotnet test", single.ToolOrSkill);
        Assert.Equal("svennijhuis/mcp-services", single.Repo);
    }

    [Fact]
    public async Task Append_creates_title_and_skips_duplicates()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcp-learnings-" + Guid.NewGuid().ToString("N"), "docs", "learnings.md");
        try
        {
            var entry = LearningsMarkdown.Format(new SquadEntry("2026-09-05", "squad", "cursor", "inherit", "squad-planner", "None", "plan", "pass", "None", "None"));
            Assert.Equal(1, await LearningsMarkdown.AppendAsync(path, [entry], CancellationToken.None));
            Assert.Equal(0, await LearningsMarkdown.AppendAsync(path, [entry], CancellationToken.None));

            var text = await File.ReadAllTextAsync(path);
            Assert.StartsWith("# Learnings\n", text, StringComparison.Ordinal);
            Assert.Equal(1, text.Split("## 2026-09-05").Length - 1);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!, recursive: true);
        }
    }
}

public class RepoRefTests
{
    [Theory]
    [InlineData("https://github.com/svennijhuis/agentPacks", "svennijhuis/agentpacks")]
    [InlineData("https://github.com/svennijhuis/agentPacks.git", "svennijhuis/agentpacks")]
    [InlineData("git@github.com:svennijhuis/agentPacks.git", "svennijhuis/agentpacks")]
    [InlineData("SvenNijhuis/AgentPacks", "svennijhuis/agentpacks")]
    [InlineData("/home/sven/src/AgentPacks/", "agentpacks")]
    [InlineData("  ", null)]
    public void Normalizes_repository_references(string input, string? expected) =>
        Assert.Equal(expected, RepoRef.Normalize(input));

    [Fact]
    public void Fingerprint_ignores_case_and_punctuation_but_not_outcome()
    {
        var a = LearningsRepository.Fingerprint(Outcome.Worked, "Run dotnet format before committing!", "x/y", [], "dotnet");
        var b = LearningsRepository.Fingerprint(Outcome.Worked, "run  DOTNET format before committing", "X/Y", [], "Dotnet");
        var c = LearningsRepository.Fingerprint(Outcome.Failed, "Run dotnet format before committing!", "x/y", [], "dotnet");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Theory]
    [InlineData("7d")]
    [InlineData("24h")]
    [InlineData("2w")]
    [InlineData("2026-01-01T00:00:00Z")]
    public void Parses_since(string value) => Assert.NotNull(LearningsTools.ParseSince(value));
}
