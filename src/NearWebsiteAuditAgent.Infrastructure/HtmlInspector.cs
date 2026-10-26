using NearWebsiteAuditAgent.Core;
using AngleSharp.Html.Parser;

namespace NearWebsiteAuditAgent.Infrastructure;

public sealed class HtmlInspector : IHtmlInspector
{
    public async Task<HtmlObservation> ParseAsync(string html, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (html.Count(character => character == '<') > 50000) throw new AuditException("html_too_complex");
        var parser = new HtmlParser(new HtmlParserOptions { IsScripting = false });
        using var document = await parser.ParseDocumentAsync(html, cancellationToken);
        var titles = document.QuerySelectorAll("title");
        var descriptions = document.QuerySelectorAll("meta").Where(e => string.Equals(e.GetAttribute("name"), "description", StringComparison.OrdinalIgnoreCase)).ToArray();
        var headings = document.QuerySelectorAll("h1");
        var canonicals = document.QuerySelectorAll("link").Where(e => (e.GetAttribute("rel") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("canonical", StringComparer.OrdinalIgnoreCase)).Select(e => e.GetAttribute("href") ?? "").ToArray();
        var robots = document.QuerySelectorAll("meta").Where(e => string.Equals(e.GetAttribute("name"), "robots", StringComparison.OrdinalIgnoreCase)).SelectMany(e => (e.GetAttribute("content") ?? "").ToLowerInvariant().Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return new(titles.Length, titles.Count(e => !string.IsNullOrWhiteSpace(e.TextContent)), descriptions.Length,
            descriptions.Count(e => !string.IsNullOrWhiteSpace(e.GetAttribute("content"))), headings.Length,
            headings.Count(e => !string.IsNullOrWhiteSpace(e.TextContent)), canonicals, robots,
            document.QuerySelectorAll("a[href]").Select(e => e.GetAttribute("href")!).ToArray());
    }
}
