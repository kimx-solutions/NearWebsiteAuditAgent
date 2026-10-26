using System.Xml;
using System.Xml.Linq;
using NearWebsiteAuditAgent.Contracts;

namespace NearWebsiteAuditAgent.Core;

internal static class SeoChecks
{
    public static void Evaluate(AuditObservation observation, CheckResults result)
    {
        if (observation.Html is { } html && observation.Main.Response is { } page)
        {
            Element(result, "seo.title", html.TitleCount == 1 && html.NonEmptyTitleCount == 1, "title");
            Element(result, "seo.description", html.DescriptionCount == 1 && html.NonEmptyDescriptionCount == 1, "meta description");
            Element(result, "seo.h1", html.H1Count == 1 && html.NonEmptyH1Count == 1, "H1");
            Element(result, "seo.canonical", html.CanonicalHrefs.Count == 1 && !string.IsNullOrWhiteSpace(html.CanonicalHrefs[0]) &&
                Uri.TryCreate(page.Url, html.CanonicalHrefs[0], out var canonical) &&
                canonical.Scheme is "http" or "https" && canonical.UserInfo.Length == 0, "HTTP(S) canonical URL");
            RobotsMeta(html.RobotsTokens, result);
        }
        Resource(observation.Robots, "seo.robots_txt", false, result);
        Resource(observation.Sitemap, "seo.sitemap", true, result);
        Links(observation, result);
    }
    private static void Element(CheckResults result, string id, bool valid, string name)
    {
        if (valid) result.Set(id, CheckOutcome.Passed);
        else result.Fail(id, Severity.Low, "invalid_" + id.Replace('.', '_'), "The page does not contain exactly one nonempty valid " + name + ".",
            "Provide exactly one appropriate " + name + " on the submitted page.");
    }
    private static void RobotsMeta(IReadOnlyList<string> tokens, CheckResults result)
    {
        var known = new[] { "all", "none", "index", "noindex", "follow", "nofollow", "noarchive", "nosnippet",
            "noimageindex", "notranslate", "indexifembedded" };
        if (tokens.Any(t => !known.Contains(t) && !t.StartsWith("max-snippet:", StringComparison.Ordinal) &&
            !t.StartsWith("max-image-preview:", StringComparison.Ordinal) && !t.StartsWith("max-video-preview:", StringComparison.Ordinal)))
        { result.Set("seo.robots_meta", CheckOutcome.NotTested, "unsupported_robots_syntax"); return; }
        if ((tokens.Contains("index") && (tokens.Contains("noindex") || tokens.Contains("none"))) ||
            (tokens.Contains("follow") && (tokens.Contains("nofollow") || tokens.Contains("none"))))
            result.Fail("seo.robots_meta", Severity.Low, "conflicting_robots", "Conflicting robots meta directives were observed.",
                "Choose consistent directives for the intended index and follow behavior.");
        else result.Set("seo.robots_meta", CheckOutcome.Passed);
        if (tokens.Any(t => t is "noindex" or "nofollow" or "none"))
            result.Finding("seo.robots_meta", Severity.Info, "robots_restriction", "The page declares an indexing or following restriction.",
                "Confirm that the restriction matches the page's intended visibility.");
    }
    private static void Resource(FetchObservation? fetch, string id, bool sitemap, CheckResults result)
    {
        if (fetch?.ErrorCode is not null || fetch?.Response is not { } response) return;
        if (response.StatusCode is 404 or 410)
        {
            result.Fail(id, Severity.Low, "missing_" + id.Replace('.', '_'), "The standard resource is absent.",
                "Provide the resource at its standard origin path when appropriate.");
            return;
        }
        if (response.StatusCode is < 200 or >= 300) return;
        if (!sitemap) { result.Set(id, CheckOutcome.Passed); return; }
        // The transport already enforces the configured byte limit (hard maximum 8 MiB).
        // Preserve a distinct inconclusive result if an alternate collector exceeds that limit.
        if (response.Body.Length > 8388608)
        { result.Set(id, CheckOutcome.NotTested, "sitemap_parse_limit"); return; }
        try
        {
            using var reader = XmlReader.Create(new StringReader(response.Body),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8388608 });
            var document = XDocument.Load(reader);
            if (document.Root?.Name.LocalName is "urlset" or "sitemapindex")
            { result.Set(id, CheckOutcome.Passed); return; }
        }
        catch (XmlException) { }
        result.Fail(id, Severity.Low, "invalid_sitemap", "The response is not a supported safe sitemap XML document.",
            "Provide a valid sitemap urlset or sitemapindex without DTDs or external entities.");
    }
    private static void Links(AuditObservation observation, CheckResults result)
    {
        const string id = "seo.links";
        if (observation.Html is null && observation.Links.Count == 0) return;
        if (observation.Scope.LinksEligible == 0 && observation.Links.Count == 0)
        { result.Set(id, CheckOutcome.NotApplicable, "no_eligible_links"); return; }
        var failed = false;
        var unknown = observation.Scope.LinksEligible > observation.Links.Count;
        foreach (var link in observation.Links)
        {
            if (link.Result.ErrorCode is not null || link.Result.Response is not { } response)
            { unknown = true; continue; }
            if (response.StatusCode is 404 or 410)
            {
                failed = true;
                result.Finding(id, Severity.Low, "broken_link", "A selected link returned HTTP " + response.StatusCode + ".",
                    "Correct or remove the broken link.", Redaction.Resource(link.Url));
            }
            else if (response.StatusCode is 401 or 403)
                result.Finding(id, Severity.Info, "restricted_link", "A selected link is access restricted.",
                    "Confirm that the restriction is expected.", Redaction.Resource(link.Url));
            else if (response.StatusCode is 429 or >= 500) unknown = true;
        }
        result.Set(id, failed ? CheckOutcome.Failed : unknown ? CheckOutcome.NotTested : CheckOutcome.Passed,
            unknown ? "incomplete_link_sample" : null, partial: unknown);
    }
}
