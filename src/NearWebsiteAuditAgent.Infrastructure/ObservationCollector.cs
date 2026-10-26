using NearWebsiteAuditAgent.Contracts;
using NearWebsiteAuditAgent.Core;

namespace NearWebsiteAuditAgent.Infrastructure;

public sealed class ObservationCollector(ISafeTargetTransport transport, IHtmlInspector html, AuditOptions options) : IAuditObservationCollector
{
    public async Task<AuditObservation> CollectAsync(Uri target, CancellationToken cancellationToken)
    {
        using var budget = new AuditBudget(options);
        var main = new FetchObservation(null, [], ErrorCode: "not_started");
        FetchObservation? https = null, upgrade = null, robots = null, sitemap = null;
        HtmlObservation? document = null;
        var links = new List<LinkObservation>();
        var eligible = 0;
        var excluded = 0;
        var selected = 0;
        try
        {
            main = await transport.FetchAsync(target, HttpMethod.Get, budget, null, cancellationToken);
            https = main.Tls is not null ? main : await transport.FetchAsync(
                new UriBuilder(target) { Scheme = "https", Port = 443 }.Uri, HttpMethod.Head, budget, null, cancellationToken);
            upgrade = target.Scheme == "http" ? main : await transport.FetchAsync(
                new UriBuilder(target) { Scheme = "http", Port = 80 }.Uri, HttpMethod.Head, budget, null, cancellationToken);
            if (main.Response is { StatusCode: >= 200 and < 300 } page && main.ErrorCode is null)
            {
                if (page.MediaType?.Equals("text/html", StringComparison.OrdinalIgnoreCase) == true ||
                    page.MediaType?.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase) == true)
                {
                    try { document = await html.ParseAsync(page.Body, cancellationToken); }
                    catch (AuditException error) { budget.Limit(error.Code); }
                }
                robots = await transport.FetchAsync(new(page.Url.GetLeftPart(UriPartial.Authority) + "/robots.txt"),
                    HttpMethod.Get, budget, page.Url, cancellationToken);
                sitemap = await transport.FetchAsync(new(page.Url.GetLeftPart(UriPartial.Authority) + "/sitemap.xml"),
                    HttpMethod.Get, budget, page.Url, cancellationToken);
                if (document is not null)
                {
                    var candidates = SelectLinks(document.Hrefs, page.Url, out excluded);
                    eligible = candidates.Count;
                    var sample = candidates.Take(options.MaxLinks).ToArray();
                    selected = sample.Length;
                    if (eligible > selected) budget.Limit("max_links");
                    foreach (var link in sample)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var probe = await transport.FetchAsync(link, HttpMethod.Head, budget, page.Url, cancellationToken);
                        if (probe.ErrorCode is null && probe.Response?.StatusCode is 405 or 501)
                            probe = await transport.FetchAsync(link, HttpMethod.Get, budget, page.Url, cancellationToken);
                        links.Add(new(link, probe));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { budget.Limit("execution_cancelled"); }
        return new()
        {
            Main = main,
            Https = https,
            HttpUpgrade = upgrade,
            Robots = robots,
            Sitemap = sitemap,
            Html = document,
            Links = links,
            Scope = budget.Snapshot() with
            {
                HtmlPagesParsed = document is null ? 0 : 1,
                LinksEligible = eligible,
                LinksSelected = selected,
                LinksAttempted = links.Count,
                LinksExcluded = excluded,
                LinksInconclusive = links.Count(l => l.Result.ErrorCode is not null || l.Result.Response?.StatusCode is 429 or >= 500),
                Limitations = ["Only the submitted page is parsed; linked pages are not crawled.",
                    "JavaScript is not executed. Online certificate revocation is not checked.",
                    "Unknown cookie authentication purpose is not assumed safe."]
            }
        };
    }
    private static IReadOnlyList<Uri> SelectLinks(IReadOnlyList<string> hrefs, Uri page, out int excluded)
    {
        excluded = 0;
        var links = new SortedDictionary<string, Uri>(StringComparer.Ordinal);
        foreach (var href in hrefs)
        {
            if (string.IsNullOrWhiteSpace(href) || href.TrimStart().StartsWith('#')) { excluded++; continue; }
            try
            {
                var target = TargetUri.Parse(new Uri(page, href).AbsoluteUri);
                if (!TargetUri.SameOrigin(page, target)) { excluded++; continue; }
                // The same document fragment is not a broken-link candidate.
                if (target.AbsoluteUri == new UriBuilder(page) { Fragment = "" }.Uri.AbsoluteUri) { excluded++; continue; }
                links.TryAdd(target.AbsoluteUri, target);
            }
            catch (Exception error) when (error is AuditException or UriFormatException) { excluded++; }
        }
        return links.Values.ToArray();
    }
}
