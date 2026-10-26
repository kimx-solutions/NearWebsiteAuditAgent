using System.Text.RegularExpressions;
using NearWebsiteAuditAgent.Contracts;

namespace NearWebsiteAuditAgent.Core;

public sealed class AuditOrchestrator(IAuditObservationCollector collector, CheckEngine checks, AuditOptions options,
    TimeProvider? timeProvider = null) : IAuditRunner
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    public async Task<AuditResult> RunAsync(AuditRequest request, CancellationToken cancellationToken, Guid? auditId = null)
    {
        var started = _clock.GetUtcNow();
        var timestamp = _clock.GetTimestamp();
        var observation = new AuditObservation();
        Uri? target = null;
        string? error = null;
        AuditStatus? forcedStatus = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            target = TargetUri.Parse(request.Url);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.MaxDurationSeconds));
            observation = await collector.CollectAsync(target, timeout.Token);
            if (cancellationToken.IsCancellationRequested) { error = "cancelled"; forcedStatus = AuditStatus.Cancelled; }
            else if (timeout.IsCancellationRequested) error = "audit_timeout";
        }
        catch (AuditException exception) { error = exception.Code; forcedStatus = AuditStatus.Rejected; }
        catch (OperationCanceledException)
        {
            error = cancellationToken.IsCancellationRequested ? "cancelled" : "audit_timeout";
            if (cancellationToken.IsCancellationRequested) forcedStatus = AuditStatus.Cancelled;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { error = "internal_error"; forcedStatus = AuditStatus.Failed; }

        AuditEvaluation evaluation;
        try { evaluation = checks.Evaluate(observation); }
        catch (RegexMatchTimeoutException)
        {
            evaluation = new(CheckCatalog.Ids.Select(id => new CheckResult(id, CheckOutcome.NotTested, ReasonCode: "parser_timeout")).ToArray(), []);
            error = "parser_timeout";
        }
        if (forcedStatus == AuditStatus.Rejected)
            evaluation = new(evaluation.Checks.Select(c => c with { Outcome = CheckOutcome.NotTested, ReasonCode = error }).ToArray(), []);
        var coverage = Scoring.GetCoverage(evaluation.Checks);
        var score = Scoring.Calculate(evaluation.Checks, evaluation.Findings);
        var initialPolicyRejection = observation.Main.ErrorCode == "target_restricted" && observation.Main.Hops.Count == 0;
        var status = forcedStatus ?? (initialPolicyRejection ? AuditStatus.Rejected : coverage.Tested == 0 ? AuditStatus.Failed :
            score.Provisional || error is not null || observation.Scope.LimitsReached.Count > 0 ? AuditStatus.Partial : AuditStatus.Succeeded);
        var errors = new[] { error, observation.Main.ErrorCode, observation.Https?.ErrorCode,
                observation.HttpUpgrade?.ErrorCode, observation.Robots?.ErrorCode, observation.Sitemap?.ErrorCode }
            .Where(code => code is not null and not "not_started").Distinct(StringComparer.Ordinal)
            .Select(code => new AuditError(code!, "The operation stopped: " + code + ".")).ToArray();
        return new()
        {
            AuditId = auditId ?? Guid.NewGuid(),
            Url = target is null ? "" : Redaction.Url(target),
            FinalUrl = observation.Main.Response is { } page ? Redaction.Url(page.Url) : null,
            StartedAt = started,
            CompletedAt = _clock.GetUtcNow(),
            DurationMs = (long)_clock.GetElapsedTime(timestamp).TotalMilliseconds,
            Status = status,
            Checks = evaluation.Checks,
            Findings = evaluation.Findings,
            Coverage = coverage,
            Score = score.Score,
            Grade = score.Grade,
            ScoreProvisional = score.Provisional,
            Scope = observation.Scope,
            Errors = errors
        };
    }
}
public sealed record ReportFiles(string JsonFile, string MarkdownFile, string MarkdownHash);
public interface IReportWriter
{
    Task<IDisposable> ReserveCapacityAsync(bool reportsRequired, CancellationToken cancellationToken);
    Task<ReportFiles> WriteAsync(AuditResult result, CancellationToken cancellationToken);
}
