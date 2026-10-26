using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NearWebsiteAuditAgent.Contracts;
using NearWebsiteAuditAgent.Core;

namespace NearWebsiteAuditAgent.Infrastructure;

public sealed class ReportWriter(AtomicFileStore store, AuditOptions options) : IReportWriter
{
    public Task<IDisposable> ReserveCapacityAsync(bool reportsRequired, CancellationToken cancellationToken)
        => store.ReserveWorkAsync((reportsRequired ? 2L * options.MaxReportBytes : 0) + 65536,
            reportsRequired ? 8 : 4, cancellationToken);
    public async Task<ReportFiles> WriteAsync(AuditResult result, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(result, ReportJson.Options);
        var markdown = Encoding.UTF8.GetBytes(Markdown(result));
        if (json.Length > options.MaxReportBytes || markdown.Length > options.MaxReportBytes)
            throw new AuditException("report_too_large");
        var stem = result.AuditId.ToString("N");
        await store.WriteAsync(stem + ".json", json, false, cancellationToken);
        await store.WriteAsync(stem + ".md", markdown, false, cancellationToken);
        return new(stem + ".json", stem + ".md", Convert.ToHexString(SHA256.HashData(markdown)).ToLowerInvariant());
    }
    public static string Markdown(AuditResult result)
    {
        var builder = new StringBuilder("# Website audit\n\n");
        builder.AppendLine("Target: " + Escape(result.Url));
        builder.AppendLine("Status: " + result.Status);
        builder.AppendLine("Score: " + (result.Score?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable") +
            (result.ScoreProvisional ? " (provisional; incomplete checks)" : " / Grade " + result.Grade));
        builder.AppendLine("\n## Coverage\n");
        builder.AppendLine($"Tested {result.Coverage.Tested}/{result.Coverage.Planned - result.Coverage.NotApplicable}; " +
            $"passed {result.Coverage.Passed}, failed {result.Coverage.Failed}, not tested {result.Coverage.NotTested}, not applicable {result.Coverage.NotApplicable}.");
        builder.AppendLine($"Pages parsed: {result.Scope.HtmlPagesParsed}; requests/probes: {result.Scope.TargetAttempts}; " +
            $"links selected/eligible: {result.Scope.LinksSelected}/{result.Scope.LinksEligible}; excluded: {result.Scope.LinksExcluded}; inconclusive: {result.Scope.LinksInconclusive}.");
        builder.AppendLine("\n## Checks\n\n| Check | Result | Completeness | Reason |\n| --- | --- | --- | --- |");
        foreach (var check in result.Checks)
            builder.AppendLine($"| {Escape(check.Id)} | {check.Outcome} | {check.Completeness} | {Escape(check.ReasonCode ?? "")} |");
        builder.AppendLine();
        foreach (var check in result.Checks.Where(c => c.Evidence.Count > 0))
            foreach (var evidence in check.Evidence)
                builder.AppendLine("- " + Escape(check.Id) + " evidence: " + Escape(evidence.Subject) + " — " + Escape(evidence.Detail));
        builder.AppendLine("\n## Findings\n");
        if (result.Findings.Count == 0) builder.AppendLine("No confirmed findings in the observed checks. Untested checks remain unresolved.");
        foreach (var finding in result.Findings)
        {
            builder.AppendLine("### " + finding.Severity + " — " + Escape(finding.Id) + "\n");
            builder.AppendLine(Escape(finding.Description));
            foreach (var evidence in finding.Evidence) builder.AppendLine("- Evidence: " + Escape(evidence.Subject) + " — " + Escape(evidence.Detail));
            builder.AppendLine("- Recommendation: " + Escape(finding.Recommendation) + "\n");
        }
        builder.AppendLine("## Limits and errors\n");
        foreach (var limit in result.Scope.LimitsReached.Concat(result.Scope.Limitations)) builder.AppendLine("- " + Escape(limit));
        foreach (var error in result.Errors) builder.AppendLine("- " + Escape(error.Code) + ": " + Escape(error.Message));
        builder.AppendLine("\nRules: " + Escape(result.RuleVersion) + "; scoring: " + Escape(result.ScoreVersion) + "; schema: " + Escape(result.SchemaVersion) + ".");
        return builder.ToString();
    }
    private static string Escape(string value)
    {
        var encoded = WebUtility.HtmlEncode(value.Replace('\r', ' ').Replace('\n', ' '));
        var result = new StringBuilder();
        foreach (var character in encoded)
        {
            if ("\\[]()!*_|#>~".Contains(character) || character == (char)96) result.Append('\\');
            result.Append(character);
        }
        return result.ToString();
    }
}
