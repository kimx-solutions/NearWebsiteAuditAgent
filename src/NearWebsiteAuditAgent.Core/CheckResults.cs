using NearWebsiteAuditAgent.Contracts;

namespace NearWebsiteAuditAgent.Core;

internal sealed class CheckResults
{
    private readonly Dictionary<string, CheckResult> _checks = CheckCatalog.Ids.ToDictionary(
        id => id, id => new CheckResult(id, CheckOutcome.NotTested, ReasonCode: "observation_unavailable", Evidence: []),
        StringComparer.Ordinal);
    private readonly List<AuditFinding> _findings = [];
    private readonly Dictionary<string, List<SafeEvidence>> _evidence = [];
    private readonly Dictionary<string, int> _omitted = [];
    public void Evidence(string id, SafeEvidence evidence)
    {
        if (!_evidence.TryGetValue(id, out var items)) _evidence[id] = items = [];
        if (items.Count < 3) items.Add(evidence);
        else _omitted[id] = _omitted.GetValueOrDefault(id) + 1;
    }
    public void Set(string id, CheckOutcome outcome, string? reason = null, bool partial = false)
        => _checks[id] = new(id, outcome, partial ? Completeness.Partial : Completeness.Complete, reason, []);
    public void Fail(string id, Severity severity, string rule, string description, string recommendation, string subject = "target")
    {
        Set(id, CheckOutcome.Failed);
        Finding(id, severity, rule, description, recommendation, subject);
    }
    public void Finding(string id, Severity severity, string rule, string description, string recommendation, string subject = "target")
        => _findings.Add(new(rule, id, id.StartsWith("seo.", StringComparison.Ordinal) ? "seo" : "security",
            severity, description, [new("observation", subject, description)], recommendation));
    public AuditEvaluation Build()
    {
        var grouped = _findings.GroupBy(f => (f.CheckId, f.Id, f.Severity)).Select(group =>
        {
            var evidence = group.SelectMany(f => f.Evidence).Distinct().OrderBy(e => e.Subject, StringComparer.Ordinal)
                .ThenBy(e => e.Detail, StringComparer.Ordinal).Take(3).ToList();
            var omitted = group.Sum(f => f.Evidence.Count) - evidence.Count;
            if (omitted > 0) evidence.Add(new("summary", "additional observations", omitted + " additional occurrences omitted."));
            return group.First() with { Evidence = evidence };
        });
        return new(CheckCatalog.Ids.Select(id => _checks[id] with
        {
            Evidence = _evidence.GetValueOrDefault(id, []).Concat(_omitted.TryGetValue(id, out var count)
                ? new[] { new SafeEvidence("summary", "additional observations", count + " additional occurrences omitted.") } : []).ToArray()
        }).ToArray(),
            grouped.OrderByDescending(f => Scoring.Weight(f.Severity)).ThenBy(f => f.CheckId, StringComparer.Ordinal)
                .ThenBy(f => f.Id, StringComparer.Ordinal).ThenBy(f => f.Evidence[0].Subject, StringComparer.Ordinal).ToArray());
    }
}
