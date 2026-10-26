using System.Security.Cryptography;
using System.Text;

namespace NearWebsiteAuditAgent.Core;

public sealed class MarketOptions
{
    public string AgentApiToken { get; set; } = "";
    public int PollIntervalSeconds { get; set; } = 60;
    public int RequestTimeoutSeconds { get; set; } = 10;
    public int MaxReadAttempts { get; set; } = 3;
    public int MaxStartAttempts { get; set; } = 3;
    public int SubmissionReserveSeconds { get; set; } = 15;
    public int MaxPages { get; set; } = 20;
    public bool AcceptPaidAssignments { get; set; }
    public Guid[] TestAssignmentIds { get; set; } = [];
}
public sealed record MarketAssignment(Guid AssignmentId, Guid JobId, Guid WorkerAgentId, string Status,
    DateTimeOffset SlaDeadlineAt, DateTimeOffset UpdatedAt, string Brief, DateTimeOffset? StartedAt = null,
    DateTimeOffset? SubmittedAt = null, string JobKind = "standard", string? Feedback = null,
    bool FeedbackTruncated = false, bool FeedbackFromBuyer = false);
public sealed class MarketException(string code, bool outcomeUnknown = false, bool pauseProcessing = false) : Exception(code)
{
    public string Code { get; } = code;
    public bool OutcomeUnknown { get; } = outcomeUnknown;
    public bool PauseProcessing { get; } = pauseProcessing;
}
public interface IMarketClient
{
    Task<IReadOnlyList<MarketAssignment>> PollAsync(CancellationToken cancellationToken);
    Task<MarketAssignment?> FindAsync(Guid assignmentId, CancellationToken cancellationToken);
    Task<DateTimeOffset> StartAsync(Guid assignmentId, CancellationToken cancellationToken);
    Task SubmitAsync(Guid assignmentId, string markdown, CancellationToken cancellationToken);
}
public enum AssignmentState
{
    Validated, Starting, Running, ReportReady, Submitting, Submitted, AwaitingReconciliation, NeedsReview, Stopped
}
public sealed record AssignmentWork
{
    public int SchemaVersion { get; init; } = 1;
    public Guid AssignmentId { get; init; }
    public Guid WorkerAgentId { get; init; }
    public Guid JobId { get; init; }
    public DateTimeOffset Revision { get; init; }
    public AssignmentState State { get; init; }
    public Guid AuditId { get; init; }
    public string? TargetUrl { get; init; }
    public string? JsonFile { get; init; }
    public string? MarkdownFile { get; init; }
    public string? MarkdownHash { get; init; }
    public string? ErrorCode { get; init; }
    public string LastPlatformStatus { get; init; } = "";
    public DateTimeOffset UpdatedAt { get; init; }
    public int StartAttempts { get; init; }
    public int AuditAttempts { get; init; }
    public Contracts.AuditStatus? ResultStatus { get; init; }
    public int ObservedChecks { get; init; }
    public string Key => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        WorkerAgentId.ToString("N") + ":" + AssignmentId.ToString("N") + ":" + Revision.UtcTicks))).ToLowerInvariant();
}
public interface IAssignmentRepository
{
    Task<IReadOnlyList<AssignmentWork>> ReadAllAsync(CancellationToken cancellationToken);
    Task SaveAsync(AssignmentWork work, CancellationToken cancellationToken);
    Task<MarketValidation?> ReadValidationAsync(CancellationToken cancellationToken);
    Task SaveValidationAsync(MarketValidation validation, CancellationToken cancellationToken);
}
public sealed record MarketValidation(Guid WorkerAgentId, Guid TestAssignmentId, DateTimeOffset VerifiedAt);
public interface IAuditRunner
{
    Task<Contracts.AuditResult> RunAsync(Contracts.AuditRequest request, CancellationToken cancellationToken, Guid? auditId = null);
}
public interface IReportContentReader
{
    Task<byte[]> ReadAsync(string name, CancellationToken cancellationToken);
}
