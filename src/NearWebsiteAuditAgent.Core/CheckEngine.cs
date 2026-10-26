namespace NearWebsiteAuditAgent.Core;

public sealed class CheckEngine
{
    public AuditEvaluation Evaluate(AuditObservation observation, CancellationToken cancellationToken = default)
    {
        var result = new CheckResults();
        cancellationToken.ThrowIfCancellationRequested();
        TlsRedirectChecks.Evaluate(observation, result);
        SecurityChecks.Evaluate(observation.Main.Response, result);
        cancellationToken.ThrowIfCancellationRequested();
        CookieChecks.Evaluate(observation.Main.Response, result);
        SeoChecks.Evaluate(observation, result);
        cancellationToken.ThrowIfCancellationRequested();
        return result.Build();
    }
}
