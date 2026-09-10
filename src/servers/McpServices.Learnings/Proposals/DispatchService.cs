using System.Data.Common;
using McpServices.Hosting;
using Microsoft.Extensions.Logging;

namespace McpServices.Learnings.Proposals;

public sealed record DispatchOutcome(Proposal Proposal, string Mode, string RequestId, DispatchResult Result);

/// <summary>
/// Applies the guardrails (policy, corroboration threshold, daily cap, whitelist) and delegates to a
/// dispatcher. Every attempt is logged in <c>dispatches</c>, successful or not.
/// </summary>
public sealed class DispatchService(LearningsRepository repository, LearningsOptions options, IEnumerable<IProposalDispatcher> dispatchers, ILogger<DispatchService> logger)
{
    private readonly Dictionary<string, IProposalDispatcher> _dispatchers = dispatchers.ToDictionary(d => d.Mode, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Modes => _dispatchers.Keys;

    public IReadOnlyDictionary<string, string?> Availability() => _dispatchers.ToDictionary(d => d.Key, d => d.Value.Unavailable(), StringComparer.OrdinalIgnoreCase);

    public IProposalDispatcher Resolve(string? mode)
    {
        var name = string.IsNullOrWhiteSpace(mode) ? options.DefaultDispatchMode : mode.Trim();
        return _dispatchers.TryGetValue(name, out var dispatcher)
            ? dispatcher
            : throw new ToolException($"Unknown dispatch mode '{name}'. Available: {string.Join(", ", _dispatchers.Keys)}.");
    }

    /// <summary>Returns the reason a proposal may not be dispatched right now, or null when the guardrails pass.</summary>
    public async Task<string?> BlockedReasonAsync(DbConnection connection, Proposal proposal, IProposalDispatcher dispatcher, CancellationToken cancellationToken)
    {
        if (proposal.Status is not (ProposalStatus.Draft or ProposalStatus.Failed))
        {
            return $"Proposal #{proposal.Id} is {LearningsRepository.StatusName(proposal.Status)}; only draft or failed proposals can be dispatched.";
        }

        if (dispatcher.Unavailable() is { } unavailable)
        {
            return unavailable;
        }

        if (!dispatcher.IsExternal)
        {
            return null;
        }

        if (options.Dispatch == DispatchPolicy.Off)
        {
            return "Dispatch is disabled (--dispatch off). Use mode dry-run or restart with --dispatch manual.";
        }

        if (proposal.Corroborations < options.MinCorroborations)
        {
            return $"Proposal has {proposal.Corroborations} corroboration(s); {options.MinCorroborations} are required (--min-corroborations). Record more learnings or use mode dry-run.";
        }

        var today = await repository.CountDispatchesSinceAsync(connection, DateTimeOffset.UtcNow.AddDays(-1), cancellationToken).ConfigureAwait(false);
        if (today >= options.MaxDispatchesPerDay)
        {
            return $"Daily dispatch cap reached ({today}/{options.MaxDispatchesPerDay}, --max-dispatches-per-day). Try again later or use mode dry-run.";
        }

        return null;
    }

    public async Task<DispatchOutcome> DispatchAsync(DbConnection connection, long proposalId, string? mode, CancellationToken cancellationToken)
    {
        var proposal = await repository.GetProposalAsync(connection, proposalId, cancellationToken).ConfigureAwait(false)
            ?? throw new ToolException($"Proposal #{proposalId} does not exist.");
        var dispatcher = Resolve(mode);
        if (await BlockedReasonAsync(connection, proposal, dispatcher, cancellationToken).ConfigureAwait(false) is { } blocked)
        {
            throw new ToolException(blocked);
        }

        var requestId = Guid.NewGuid().ToString("N");
        try
        {
            var result = await dispatcher.DispatchAsync(proposal, requestId, cancellationToken).ConfigureAwait(false);
            await repository.InsertDispatchAsync(connection, proposal.Id, dispatcher.Mode, requestId, result.ExternalId, result.Status, result.Detail, cancellationToken).ConfigureAwait(false);
            if (dispatcher.IsExternal)
            {
                var status = result.PrUrl is not null ? ProposalStatus.PrOpened : result.Status == "no-changes" ? ProposalStatus.Draft : ProposalStatus.Dispatched;
                await repository.UpdateProposalAsync(connection, proposal.Id, status, dispatcher.Mode, result.ExternalId, result.PrUrl, null, cancellationToken).ConfigureAwait(false);
            }

            var updated = (await repository.GetProposalAsync(connection, proposal.Id, cancellationToken).ConfigureAwait(false))!;
            return new DispatchOutcome(updated, dispatcher.Mode, requestId, result);
        }
        catch (Exception ex) when (ex is ToolException or HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Dispatch of proposal {Id} via {Mode} failed: {Message}", proposal.Id, dispatcher.Mode, ex.Message);
            await repository.InsertDispatchAsync(connection, proposal.Id, dispatcher.Mode, requestId, null, "failed", ex.Message, cancellationToken).ConfigureAwait(false);
            if (dispatcher.IsExternal)
            {
                await repository.UpdateProposalAsync(connection, proposal.Id, ProposalStatus.Failed, dispatcher.Mode, null, null, ex.Message, cancellationToken).ConfigureAwait(false);
            }

            throw new ToolException($"Dispatch via {dispatcher.Mode} failed: {ex.Message}", ex);
        }
    }

    /// <summary>Polls the remote run (when the mode supports it) and stores status and PR URL on the proposal.</summary>
    public async Task<(Proposal Proposal, DispatchPoll? Poll, IReadOnlyList<DispatchRow> History)> StatusAsync(DbConnection connection, long proposalId, CancellationToken cancellationToken)
    {
        var proposal = await repository.GetProposalAsync(connection, proposalId, cancellationToken).ConfigureAwait(false)
            ?? throw new ToolException($"Proposal #{proposalId} does not exist.");
        DispatchPoll? poll = null;
        if (proposal.Status is ProposalStatus.Dispatched && proposal.DispatchMode is not null && _dispatchers.TryGetValue(proposal.DispatchMode, out var dispatcher))
        {
            poll = await dispatcher.PollAsync(proposal, cancellationToken).ConfigureAwait(false);
            if (poll is not null && (poll.Mapped != proposal.Status || poll.PrUrl is not null))
            {
                await repository.UpdateProposalAsync(connection, proposal.Id, poll.Mapped, null, null, poll.PrUrl, poll.Mapped == ProposalStatus.Failed ? poll.Detail ?? poll.RemoteStatus : null, cancellationToken).ConfigureAwait(false);
                proposal = (await repository.GetProposalAsync(connection, proposal.Id, cancellationToken).ConfigureAwait(false))!;
            }
        }

        var history = await repository.ListDispatchesAsync(connection, proposal.Id, cancellationToken).ConfigureAwait(false);
        return (proposal, poll, history);
    }
}
