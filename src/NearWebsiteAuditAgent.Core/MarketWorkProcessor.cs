using System.Security.Cryptography;
using System.Text;
using NearWebsiteAuditAgent.Contracts;

namespace NearWebsiteAuditAgent.Core;

public sealed class MarketWorkProcessor(IMarketClient client, IAssignmentRepository repository, IAuditRunner runner,
    IReportWriter reports, IReportContentReader store, MarketOptions options, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    public async Task<AssignmentWork?> ProcessAsync(MarketAssignment assignment, CancellationToken cancellationToken)
    {
        var validation = await repository.ReadValidationAsync(cancellationToken);
        var isTest = options.TestAssignmentIds.Contains(assignment.AssignmentId);
        if (!isTest && !(options.AcceptPaidAssignments && validation?.WorkerAgentId == assignment.WorkerAgentId)) return null;
        var records = await repository.ReadAllAsync(cancellationToken);
        var history = records.Where(w => w.AssignmentId == assignment.AssignmentId && w.WorkerAgentId == assignment.WorkerAgentId).ToArray();
        var work = history.FirstOrDefault(w => w.Revision == assignment.SlaDeadlineAt);
        if (work is not null && assignment.Status != "in_progress")
        {
            work = Reconcile(work, assignment.Status);
            await PersistAsync(work, cancellationToken);
            await RecordValidationAsync(work, isTest, cancellationToken);
            return work;
        }
        if (work is null)
        {
            if (assignment.Status != "in_progress") return null;
            work = await PrepareAsync(assignment, history, cancellationToken);
        }
        if (work.State is AssignmentState.Submitting or AssignmentState.AwaitingReconciliation)
        {
            work = work with { State = AssignmentState.AwaitingReconciliation, ErrorCode = "submission_outcome_unknown" };
            await PersistAsync(work, cancellationToken);
            return work;
        }
        if (work.State is AssignmentState.NeedsReview or AssignmentState.Stopped or AssignmentState.Submitted) return work;
        if (assignment.SlaDeadlineAt - _clock.GetUtcNow() <= TimeSpan.FromSeconds(options.SubmissionReserveSeconds))
            return await ReviewAsync(work, "sla_insufficient", cancellationToken, AssignmentState.Stopped);

        using var capacity = await reports.ReserveCapacityAsync(work.State != AssignmentState.ReportReady, cancellationToken);
        if (work.State is AssignmentState.Validated or AssignmentState.Starting)
            work = await StartWorkAsync(work, assignment, cancellationToken);
        if (work.State == AssignmentState.Running)
            work = await RunWorkAsync(work, assignment, cancellationToken);
        if (work.State == AssignmentState.ReportReady)
            work = await SubmitReadyAsync(work, cancellationToken);
        await RecordValidationAsync(work, isTest, cancellationToken);
        return work;
    }

    private async Task<AssignmentWork> StartWorkAsync(AssignmentWork work, MarketAssignment assignment, CancellationToken cancellationToken)
    {
        if (work.StartAttempts >= options.MaxStartAttempts) return await ReviewAsync(work, "start_attempt_limit", cancellationToken);
        work = work with { State = AssignmentState.Starting, StartAttempts = work.StartAttempts + 1 };
        await PersistAsync(work, cancellationToken);
        try { await client.StartAsync(assignment.AssignmentId, cancellationToken); }
        catch (MarketException error)
        {
            await ReviewAsync(work, error.Code, CancellationToken.None);
            if (error.PauseProcessing) throw;
            return work with { State = AssignmentState.NeedsReview, ErrorCode = error.Code };
        }
        work = work with { State = AssignmentState.Running };
        await PersistAsync(work, cancellationToken);

        return work;
    }
    private async Task<AssignmentWork> RunWorkAsync(AssignmentWork work, MarketAssignment assignment, CancellationToken cancellationToken)
    {
        if (work.AuditAttempts >= 3) return await ReviewAsync(work, "audit_attempt_limit", cancellationToken);
        work = work with { AuditId = work.AuditAttempts > 0 ? Guid.NewGuid() : work.AuditId, AuditAttempts = work.AuditAttempts + 1 };
        await PersistAsync(work, cancellationToken);
        var remaining = assignment.SlaDeadlineAt - _clock.GetUtcNow() - TimeSpan.FromSeconds(options.SubmissionReserveSeconds);
        if (remaining <= TimeSpan.Zero) return await ReviewAsync(work, "sla_insufficient", cancellationToken, AssignmentState.Stopped);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(remaining < TimeSpan.FromMinutes(5) ? remaining : TimeSpan.FromMinutes(5));
        var result = await runner.RunAsync(new(work.TargetUrl!), deadline.Token, work.AuditId);
        if (cancellationToken.IsCancellationRequested || result.Status == AuditStatus.Cancelled)
        {
            work = work with { ErrorCode = "cancelled" };
            await PersistAsync(work, CancellationToken.None);
            return work;
        }
        var files = await reports.WriteAsync(result, cancellationToken);
        work = work with
        {
            State = AssignmentState.ReportReady,
            JsonFile = files.JsonFile,
            MarkdownFile = files.MarkdownFile,
            MarkdownHash = files.MarkdownHash,
            ResultStatus = result.Status,
            ObservedChecks = result.Coverage.Tested
        };
        await PersistAsync(work, cancellationToken);

        return work;
    }

    private async Task<AssignmentWork> PrepareAsync(MarketAssignment assignment, IReadOnlyList<AssignmentWork> history,
        CancellationToken cancellationToken)
    {
        var work = new AssignmentWork
        {
            AssignmentId = assignment.AssignmentId,
            WorkerAgentId = assignment.WorkerAgentId,
            JobId = assignment.JobId,
            Revision = assignment.SlaDeadlineAt,
            AuditId = Guid.NewGuid(),
            State = AssignmentState.Validated,
            LastPlatformStatus = assignment.Status,
            UpdatedAt = _clock.GetUtcNow()
        };
        if (assignment.JobKind is not ("standard" or "instant")) return await ReviewAsync(work, "unsupported_job_kind", cancellationToken);
        var brief = assignment.Brief;
        if (history.Count > 0)
        {
            if (history.Count >= 2 || !history.Any(w => w.State == AssignmentState.Submitted) ||
                assignment.SubmittedAt is null || !assignment.FeedbackFromBuyer || assignment.FeedbackTruncated || assignment.Feedback is null)
                return await ReviewAsync(work, "revision_unresolved", cancellationToken);
            brief = assignment.Feedback;
        }
        else if (assignment.StartedAt is not null)
            return await ReviewAsync(work, "started_assignment_without_journal", cancellationToken);
        try
        {
            var input = AuditInput.Parse(brief);
            work = work with { TargetUrl = TargetUri.Parse(input.Url).AbsoluteUri };
        }
        catch (AuditException) { return await ReviewAsync(work, "assignment_input_invalid", cancellationToken); }
        await PersistAsync(work, cancellationToken);
        return work;
    }
    private async Task<AssignmentWork> SubmitReadyAsync(AssignmentWork work, CancellationToken cancellationToken)
    {
        var remote = await client.FindAsync(work.AssignmentId, cancellationToken);
        if (remote is null || remote.WorkerAgentId != work.WorkerAgentId || remote.SlaDeadlineAt != work.Revision)
            return await ReviewAsync(work, "revision_unresolved", cancellationToken);
        if (remote.Status != "in_progress")
            return await ReviewAsync(work, "remote_already_finalized", cancellationToken, AssignmentState.Stopped);
        var remaining = remote.SlaDeadlineAt - _clock.GetUtcNow();
        if (remaining <= TimeSpan.FromSeconds(options.SubmissionReserveSeconds))
            return await ReviewAsync(work, "sla_insufficient", cancellationToken, AssignmentState.Stopped);
        var markdown = await store.ReadAsync(work.MarkdownFile!, cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(markdown)).ToLowerInvariant();
        if (hash != work.MarkdownHash) throw new AuditException("storage_unavailable");
        cancellationToken.ThrowIfCancellationRequested();
        work = work with { State = AssignmentState.Submitting };
        await PersistAsync(work, cancellationToken); // durable intent BEFORE the non-idempotent POST
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(remaining < TimeSpan.FromMinutes(5) ? remaining : TimeSpan.FromMinutes(5));
            await client.SubmitAsync(work.AssignmentId, new UTF8Encoding(false, true).GetString(markdown), deadline.Token);
            work = work with { State = AssignmentState.Submitted, LastPlatformStatus = "submitted", ErrorCode = null };
            await PersistAsync(work, CancellationToken.None);
        }
        catch (MarketException error)
        {
            work = work with
            {
                State = error.OutcomeUnknown ? AssignmentState.AwaitingReconciliation : AssignmentState.NeedsReview,
                ErrorCode = error.Code
            };
            await PersistAsync(work, CancellationToken.None);
            if (error.PauseProcessing) throw;
        }
        catch (OperationCanceledException)
        {
            work = work with { State = AssignmentState.AwaitingReconciliation, ErrorCode = "submission_outcome_unknown" };
            await PersistAsync(work, CancellationToken.None);
        }
        return work;
    }
    private async Task RecordValidationAsync(AssignmentWork work, bool isTest, CancellationToken cancellationToken)
    {
        if (!isTest || work.State != AssignmentState.Submitted || work.ObservedChecks == 0 ||
            work.ResultStatus is not (AuditStatus.Succeeded or AuditStatus.Partial) || cancellationToken.IsCancellationRequested) return;
        var remote = await client.FindAsync(work.AssignmentId, cancellationToken);
        if (remote?.WorkerAgentId == work.WorkerAgentId && remote.SlaDeadlineAt == work.Revision &&
            remote.Status is "submitted" or "accepted")
            await repository.SaveValidationAsync(new(work.WorkerAgentId, work.AssignmentId, _clock.GetUtcNow()), cancellationToken);
    }
    private static AssignmentWork Reconcile(AssignmentWork work, string platformStatus)
        => work with
        {
            State = platformStatus is "submitted" or "accepted" && work.MarkdownFile is not null
                ? AssignmentState.Submitted : AssignmentState.Stopped,
            LastPlatformStatus = platformStatus,
            ErrorCode = null
        };
    private async Task<AssignmentWork> ReviewAsync(AssignmentWork work, string reason, CancellationToken cancellationToken,
        AssignmentState state = AssignmentState.NeedsReview)
    {
        work = work with { State = state, ErrorCode = reason };
        await PersistAsync(work, cancellationToken);
        return work;
    }
    private Task PersistAsync(AssignmentWork work, CancellationToken cancellationToken)
        => repository.SaveAsync(work with { UpdatedAt = _clock.GetUtcNow() }, cancellationToken);
}
