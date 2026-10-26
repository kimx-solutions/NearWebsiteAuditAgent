using NearWebsiteAuditAgent.Core;
using NearWebsiteAuditAgent.Infrastructure;

namespace NearWebsiteAuditAgent.Worker;

public sealed class MarketExecution(IMarketClient client, IAssignmentRepository repository, MarketWorkProcessor processor,
    AtomicFileStore store, MarketOptions options, StorageOptions storage, ILogger<MarketExecution> logger,
    TimeProvider? timeProvider = null) : IExecution
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        await JournalRetention.CleanupAsync(repository, store, storage.RetentionDays, cancellationToken);
        var processed = new HashSet<Guid>();
        foreach (var assignment in (await PollAssignmentsAsync(cancellationToken)).OrderBy(a => a.SlaDeadlineAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await processor.ProcessAsync(assignment, cancellationToken);
            processed.Add(assignment.AssignmentId);
            if (result is not null) logger.LogInformation("assignment_state {AssignmentId} {State} {Code}",
                assignment.AssignmentId, result.State, result.ErrorCode);
        }
        var validation = await repository.ReadValidationAsync(cancellationToken);
        var pending = (await repository.ReadAllAsync(cancellationToken)).Where(w =>
            w.State is AssignmentState.Validated or AssignmentState.Starting or AssignmentState.Running or AssignmentState.ReportReady or
                AssignmentState.Submitting or AssignmentState.AwaitingReconciliation ||
            (validation is null && w.State == AssignmentState.Submitted && options.TestAssignmentIds.Contains(w.AssignmentId)));
        foreach (var work in pending.GroupBy(w => w.AssignmentId).Select(g => g.OrderByDescending(w => w.Revision).First()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!processed.Add(work.AssignmentId)) continue;
            var remote = await client.FindAsync(work.AssignmentId, cancellationToken);
            if (remote is not null) await processor.ProcessAsync(remote, cancellationToken);
        }
        await JournalRetention.CleanupAsync(repository, store, storage.RetentionDays, cancellationToken);
    }
    private async Task<IReadOnlyList<MarketAssignment>> PollAssignmentsAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(options.PollIntervalSeconds * 2, 60, 300)), _clock);
        using var poll = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        return await client.PollAsync(poll.Token);
    }
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var instance = store.AcquireLock();
            while (!cancellationToken.IsCancellationRequested)
            {
                try { await RunCycleAsync(cancellationToken); }
                catch (MarketException error)
                {
                    logger.LogWarning("market_error {Code}", error.Code);
                    if (error.PauseProcessing) return 1;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { logger.LogWarning("market_error {Code}", "poll_timeout"); }
                await Task.Delay(TimeSpan.FromSeconds(options.PollIntervalSeconds), cancellationToken);
            }
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return 0; }
        catch (AuditException error) { logger.LogError("market_error {Code}", error.Code); return 1; }
    }
}
