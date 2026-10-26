using NearWebsiteAuditAgent.Contracts;

namespace NearWebsiteAuditAgent.Core;

public sealed class AuditOptions
{
    public int MaxDurationSeconds { get; set; } = 60;
    public int RequestTimeoutSeconds { get; set; } = 10;
    public int DnsTimeoutSeconds { get; set; } = 3;
    public int ConnectTimeoutSeconds { get; set; } = 5;
    public int MaxRedirects { get; set; } = 5;
    public int MaxTargetAttempts { get; set; } = 40;
    public int MaxLinks { get; set; } = 10;
    public int MaxHtmlPages { get; set; } = 1;
    public int MaxResponseBytes { get; set; } = 2097152;
    public int MaxTotalResponseBytes { get; set; } = 8388608;
    public int MaxResponseHeaderBytes { get; set; } = 32768;
    public int MaxConcurrentAudits { get; set; } = 1;
    public int MaxConcurrentRequestsPerAudit { get; set; } = 2;
    public int MaxReportBytes { get; set; } = 196608;
}

public sealed record HttpObservation(Uri Url, int StatusCode, IReadOnlyDictionary<string, string[]> Headers,
    string Body = "", string? MediaType = null)
{
    public IReadOnlyList<string> Values(string name)
        => Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value ?? [];
    public string Header(string name) => string.Join(", ", Values(name));
}
public sealed record RedirectHop(Uri Url, int StatusCode);
public sealed record TlsObservation(bool CertificatePresented, bool CertificateValid,
    bool Expired, bool NotYetValid, bool HostnameMatches, bool ChainTrusted, string? Protocol);
public sealed record FetchObservation(HttpObservation? Response, IReadOnlyList<RedirectHop> Hops,
    TlsObservation? Tls = null, string? ErrorCode = null);
public sealed record LinkObservation(Uri Url, FetchObservation Result);
public sealed record HtmlObservation(int TitleCount, int NonEmptyTitleCount,
    int DescriptionCount, int NonEmptyDescriptionCount, int H1Count, int NonEmptyH1Count,
    IReadOnlyList<string> CanonicalHrefs, IReadOnlyList<string> RobotsTokens, IReadOnlyList<string> Hrefs);
public sealed record AuditObservation
{
    public FetchObservation Main { get; init; } = new(null, [], ErrorCode: "not_started");
    public FetchObservation? Https { get; init; }
    public FetchObservation? HttpUpgrade { get; init; }
    public FetchObservation? Robots { get; init; }
    public FetchObservation? Sitemap { get; init; }
    public HtmlObservation? Html { get; init; }
    public IReadOnlyList<LinkObservation> Links { get; init; } = [];
    public AuditScope Scope { get; init; } = new();
}
public sealed record AuditEvaluation(IReadOnlyList<CheckResult> Checks, IReadOnlyList<AuditFinding> Findings);
public interface IAuditObservationCollector
{
    Task<AuditObservation> CollectAsync(Uri target, CancellationToken cancellationToken);
}
public interface IHtmlInspector
{
    Task<HtmlObservation> ParseAsync(string html, CancellationToken cancellationToken);
}
public static class CheckCatalog
{
    public static IReadOnlyList<string> Ids { get; } = Array.AsReadOnly(new[]
    {
        "tls.https", "tls.certificate", "tls.protocol", "headers.hsts", "headers.csp",
        "headers.framing", "headers.nosniff", "headers.referrer", "headers.permissions",
        "cookies.secure", "cookies.httponly", "cookies.samesite",
        "redirects.upgrade", "redirects.chain", "seo.title", "seo.description", "seo.h1",
        "seo.canonical", "seo.robots_meta", "seo.robots_txt", "seo.sitemap", "seo.links"
    });
}
