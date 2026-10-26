using System.Text.Json;
using System.Text.Json.Serialization;

namespace NearWebsiteAuditAgent.Contracts;

public enum AuditStatus { Pending, Running, Succeeded, Partial, Rejected, Failed, Cancelled }
public enum CheckOutcome { Passed, Failed, NotTested, NotApplicable }
public enum Completeness { Complete, Partial }
public enum Severity { Info, Low, Medium, High, Critical }

public sealed record AuditRequest(string Url);
public sealed record SafeEvidence(string Kind, string Subject, string Detail);
public sealed record CheckResult(string Id, CheckOutcome Outcome, Completeness Completeness = Completeness.Complete,
    string? ReasonCode = null, IReadOnlyList<SafeEvidence>? Evidence = null)
{
    public IReadOnlyList<SafeEvidence> Evidence { get; init; } = Evidence ?? [];
}
public sealed record AuditFinding(string Id, string CheckId, string Category, Severity Severity,
    string Description, IReadOnlyList<SafeEvidence> Evidence, string Recommendation);
public sealed record AuditError(string Code, string Message, string? CheckId = null);
public sealed record Coverage(int Planned, int Passed, int Failed, int NotTested, int NotApplicable, int Tested);
public sealed record AuditScope
{
    public int HtmlPagesParsed { get; init; }
    public int TargetAttempts { get; init; }
    public long DecodedBodyBytes { get; init; }
    public int LinksEligible { get; init; }
    public int LinksSelected { get; init; }
    public int LinksAttempted { get; init; }
    public int LinksInconclusive { get; init; }
    public int LinksExcluded { get; init; }
    public IReadOnlyList<string> LimitsReached { get; init; } = [];
    public IReadOnlyList<string> Limitations { get; init; } = [];
}
public sealed record AuditResult
{
    public string SchemaVersion { get; init; } = "1.0";
    public Guid AuditId { get; init; }
    public string Url { get; init; } = "";
    public string? FinalUrl { get; init; }
    public AuditStatus Status { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public long DurationMs { get; init; }
    public int? Score { get; init; }
    public string? Grade { get; init; }
    public bool ScoreProvisional { get; init; } = true;
    public string RuleVersion { get; init; } = "rules-v1";
    public string ScoreVersion { get; init; } = "score-v1";
    public IReadOnlyList<CheckResult> Checks { get; init; } = [];
    public IReadOnlyList<AuditFinding> Findings { get; init; } = [];
    public Coverage Coverage { get; init; } = new(0, 0, 0, 0, 0, 0);
    public AuditScope Scope { get; init; } = new();
    public IReadOnlyList<AuditError> Errors { get; init; } = [];
}
public static class ReportJson
{
    public static JsonSerializerOptions Options { get; } = Create();
    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            MaxDepth = 32
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
