using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using McpServices.Hosting;
using McpServices.Storage;
using Microsoft.Extensions.Logging;

namespace McpServices.Learnings.Proposals;

public sealed record DispatchResult(string Status, string? ExternalId, string? PrUrl, string Detail);

public sealed record DispatchPoll(string RemoteStatus, ProposalStatus Mapped, string? PrUrl, string? Detail);

/// <summary>One way to hand a proposal to something that can open a pull request.</summary>
public interface IProposalDispatcher
{
    string Mode { get; }

    /// <summary>Whether this mode counts against the daily cap and corroboration threshold.</summary>
    bool IsExternal { get; }

    /// <summary>Null when usable, otherwise a user-facing reason (missing key, missing binary).</summary>
    string? Unavailable();

    Task<DispatchResult> DispatchAsync(Proposal proposal, string requestId, CancellationToken cancellationToken);

    Task<DispatchPoll?> PollAsync(Proposal proposal, CancellationToken cancellationToken);
}

/// <summary>Writes the proposal to a markdown file; always available, never calls anything.</summary>
public sealed class DryRunDispatcher(LearningsOptions options) : IProposalDispatcher
{
    public string Mode => "dry-run";

    public bool IsExternal => false;

    public string? Unavailable() => null;

    public async Task<DispatchResult> DispatchAsync(Proposal proposal, string requestId, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.ProposalsDirectory);
        var path = Path.Combine(options.ProposalsDirectory, $"proposal-{proposal.Id}.md");
        await File.WriteAllTextAsync(path, ImprovementPrompt.ToMarkdown(proposal), cancellationToken).ConfigureAwait(false);
        return new DispatchResult("written", path, null, $"Proposal written to {path}. Paste the prompt into Cursor, or run dispatch_proposal with mode cursor-cloud.");
    }

    public Task<DispatchPoll?> PollAsync(Proposal proposal, CancellationToken cancellationToken) => Task.FromResult<DispatchPoll?>(null);
}

/// <summary>
/// Cursor Cloud Agents API: <c>POST /v0/agents</c> with basic auth (<c>CURSOR_API_KEY</c> as user name),
/// <c>target.autoCreatePr = true</c>; status via <c>GET /v0/agents/{id}</c>. Retries 5xx/429 with backoff.
/// </summary>
public sealed class CursorCloudDispatcher(HttpClient http, LearningsOptions options, ILogger<CursorCloudDispatcher> logger) : IProposalDispatcher
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(8)];

    public string Mode => "cursor-cloud";

    public bool IsExternal => true;

    public string? Unavailable() =>
        options.HasCursorApiKey ? null : "CURSOR_API_KEY is not set in the server's environment (create an agent API key under Cursor Dashboard > Cloud Agents > My Settings).";

    public async Task<DispatchResult> DispatchAsync(Proposal proposal, string requestId, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["prompt"] = new { text = proposal.Prompt },
            ["source"] = new { repository = RepoRef.ToUrl(proposal.TargetRepo), @ref = options.BaseRef },
            ["target"] = new { autoCreatePr = true, branchName = $"cursor/learnings-{proposal.Id}-{requestId[..6]}", skipReviewerRequest = false },
        };
        if (!string.IsNullOrEmpty(options.CursorModel))
        {
            body["model"] = options.CursorModel;
        }

        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.CursorApiBase, "/v0/agents")) { Content = JsonContent.Create(body) };
            request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);
            return request;
        }, cancellationToken).ConfigureAwait(false);

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ToolException($"Cursor API returned {(int)response.StatusCode}: {Text.Truncate(text, 500)}");
        }

        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        var status = root.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : "CREATING";
        var url = root.TryGetProperty("target", out var target) && target.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
        return new DispatchResult(status ?? "CREATING", id, null, url is null ? $"Cloud agent {id} created." : $"Cloud agent {id} created: {url}");
    }

    public async Task<DispatchPoll?> PollAsync(Proposal proposal, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(proposal.ExternalId))
        {
            return null;
        }

        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, new Uri(options.CursorApiBase, $"/v0/agents/{Uri.EscapeDataString(proposal.ExternalId)}")), cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ToolException($"Cursor API returned {(int)response.StatusCode}: {Text.Truncate(text, 500)}");
        }

        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        var status = (root.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null) ?? "UNKNOWN";
        string? prUrl = null;
        string? summary = root.TryGetProperty("summary", out var summaryElement) ? summaryElement.GetString() : null;
        if (root.TryGetProperty("target", out var target))
        {
            prUrl = target.TryGetProperty("prUrl", out var pr) ? pr.GetString() : null;
        }

        var mapped = status.ToUpperInvariant() switch
        {
            "FINISHED" when prUrl is not null => ProposalStatus.PrOpened,
            "FINISHED" => ProposalStatus.Dispatched,
            "ERROR" or "EXPIRED" => ProposalStatus.Failed,
            _ => ProposalStatus.Dispatched,
        };
        return new DispatchPoll(status, mapped, prUrl, summary);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> factory, CancellationToken cancellationToken)
    {
        var key = Environment.GetEnvironmentVariable("CURSOR_API_KEY") ?? throw new ToolException(Unavailable()!);
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(key + ":"));
        for (var attempt = 0; ; attempt++)
        {
            using var request = factory();
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
            HttpResponseMessage? response = null;
            try
            {
                response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if ((int)response.StatusCode < 500 && response.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    return response;
                }

                if (attempt >= Backoff.Length)
                {
                    return response;
                }

                logger.LogWarning("Cursor API returned {Status}; retrying in {Delay}", (int)response.StatusCode, Backoff[attempt]);
                response.Dispose();
            }
            catch (HttpRequestException ex) when (attempt < Backoff.Length)
            {
                response?.Dispose();
                logger.LogWarning("Cursor API unreachable ({Message}); retrying in {Delay}", ex.Message, Backoff[attempt]);
            }

            await Task.Delay(Backoff[attempt], cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Local mode: clones (or reuses a configured checkout of) the target repository into a worktree,
/// runs the Cursor CLI headlessly on the prompt, commits, pushes and opens a draft PR with <c>gh</c>.
/// </summary>
public sealed partial class CursorCliDispatcher(LearningsOptions options, ILogger<CursorCliDispatcher> logger) : IProposalDispatcher
{
    private static readonly TimeSpan AgentTimeout = TimeSpan.FromMinutes(30);

    public string Mode => "cursor-cli";

    public bool IsExternal => true;

    public string? Unavailable()
    {
        var missing = new[] { options.CursorCliCommand, "git", "gh" }.Where(c => !OnPath(c)).ToList();
        return missing.Count == 0 ? null : $"Not on PATH: {string.Join(", ", missing)}. Install the Cursor CLI (curl https://cursor.com/install -fsS | bash) and GitHub CLI.";
    }

    public async Task<DispatchResult> DispatchAsync(Proposal proposal, string requestId, CancellationToken cancellationToken)
    {
        var branch = $"cursor/learnings-{proposal.Id}-{requestId[..6]}";
        var workDir = Path.Combine(Path.GetTempPath(), "mcp-learnings", $"proposal-{proposal.Id}-{requestId[..6]}");
        Directory.CreateDirectory(Path.GetDirectoryName(workDir)!);
        var cloneUrl = Environment.GetEnvironmentVariable("MCP_LEARNINGS_CLONE_URL") ?? RepoRef.ToUrl(proposal.TargetRepo);

        try
        {
            await RunAsync("git", ["clone", "--quiet", "--depth", "50", "--branch", options.BaseRef, cloneUrl, workDir], Path.GetTempPath(), TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
            await RunAsync("git", ["checkout", "-q", "-b", branch], workDir, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

            var promptFile = Path.Combine(workDir, ".mcp-learnings-prompt.md");
            await File.WriteAllTextAsync(promptFile, proposal.Prompt, cancellationToken).ConfigureAwait(false);
            var agentOutput = await RunAsync(options.CursorCliCommand, ["-p", "--force", "--output-format", "text", proposal.Prompt], workDir, AgentTimeout, cancellationToken).ConfigureAwait(false);
            File.Delete(promptFile);

            var status = await RunAsync("git", ["status", "--porcelain"], workDir, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(status))
            {
                return new DispatchResult("no-changes", null, null, "The agent made no changes. Agent output: " + Text.Truncate(agentOutput, 1500));
            }

            await RunAsync("git", ["add", "-A"], workDir, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            await RunAsync("git", ["-c", "user.name=mcp-learnings", "-c", "user.email=mcp-learnings@localhost", "commit", "-q", "-m", proposal.Title, "-m", $"Learnings: {string.Join(", ", proposal.LearningIds.Select(id => "#" + id))}"], workDir, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            await RunAsync("git", ["push", "-q", "-u", "origin", branch], workDir, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);

            var body = $"Automated improvement proposal #{proposal.Id} from mcp-learnings.\n\n{proposal.Rationale}\n\nLearnings: {string.Join(", ", proposal.LearningIds.Select(id => "#" + id))}\n\nAcceptance criteria:\n{string.Join('\n', proposal.AcceptanceCriteria.Select(c => "- " + c))}";
            var prOutput = await RunAsync("gh", ["pr", "create", "--draft", "--title", proposal.Title, "--body", body, "--head", branch, "--base", options.BaseRef], workDir, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
            var prUrl = PrUrl().Match(prOutput) is { Success: true } m ? m.Value : null;
            return new DispatchResult(prUrl is null ? "pushed" : "pr_opened", branch, prUrl, Text.Truncate(prOutput, 1000));
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDir))
                {
                    Directory.Delete(workDir, recursive: true);
                }
            }
            catch (IOException ex)
            {
                logger.LogWarning("Could not clean {Dir}: {Message}", workDir, ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning("Could not clean {Dir}: {Message}", workDir, ex.Message);
            }
        }
    }

    public Task<DispatchPoll?> PollAsync(Proposal proposal, CancellationToken cancellationToken) => Task.FromResult<DispatchPoll?>(null);

    private static bool OnPath(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", string.Empty } : [string.Empty];
        return Path.IsPathRooted(command)
            ? File.Exists(command)
            : path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Any(dir => extensions.Any(ext => File.Exists(Path.Combine(dir, command + ext))));
    }

    private static async Task<string> RunAsync(string command, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(command)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new ToolException($"Could not start '{command}'.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = process.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new ToolException($"'{command} {(arguments.Count > 0 ? arguments[0] : string.Empty)}' timed out after {timeout.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutes.");
        }

        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new ToolException($"'{command} {string.Join(' ', arguments.Take(2))}' failed ({process.ExitCode}): {Text.Truncate(SecretRedactor.Redact(error.Length > 0 ? error : output), 800)}");
        }

        return output;
    }

    [GeneratedRegex(@"https://\S+/pull/\d+")]
    private static partial Regex PrUrl();
}

/// <summary>POSTs the proposal to a webhook (e.g. a Cursor Automation) with a bearer token from the environment.</summary>
public sealed class WebhookDispatcher(HttpClient http, LearningsOptions options) : IProposalDispatcher
{
    public string Mode => "webhook";

    public bool IsExternal => true;

    public string? Unavailable() =>
        options.WebhookUrl is null ? "No webhook configured (--webhook <url> or MCP_LEARNINGS_WEBHOOK_URL)." : null;

    public async Task<DispatchResult> DispatchAsync(Proposal proposal, string requestId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.WebhookUrl)
        {
            Content = JsonContent.Create(new
            {
                type = "mcp-learnings.proposal",
                requestId,
                proposal = new { proposal.Id, proposal.Title, proposal.TargetRepo, proposal.Rationale, proposal.LearningIds, proposal.LikelyFiles, proposal.AcceptanceCriteria, proposal.Prompt, proposal.Corroborations },
            }, options: ToolJson.Options),
        };
        if (Environment.GetEnvironmentVariable("MCP_LEARNINGS_WEBHOOK_TOKEN") is { Length: > 0 } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ToolException($"Webhook returned {(int)response.StatusCode}: {Text.Truncate(text, 500)}");
        }

        return new DispatchResult("accepted", requestId, null, Text.Truncate(text, 500));
    }

    public Task<DispatchPoll?> PollAsync(Proposal proposal, CancellationToken cancellationToken) => Task.FromResult<DispatchPoll?>(null);
}
