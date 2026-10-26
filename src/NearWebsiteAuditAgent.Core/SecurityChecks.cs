using System.Text.RegularExpressions;
using NearWebsiteAuditAgent.Contracts;

namespace NearWebsiteAuditAgent.Core;

internal static class SecurityChecks
{
    public static void Evaluate(HttpObservation? page, CheckResults result)
    {
        if (page is null) return;
        Hsts(page, result);
        var cspValues = page.Values("Content-Security-Policy");
        var csp = cspValues.Count == 1 ? ParseCsp(cspValues[0]) : null;
        if (cspValues.Count == 0 || cspValues.All(string.IsNullOrWhiteSpace))
            result.Fail("headers.csp", Severity.Medium, "missing_csp", "An enforced CSP header is missing.",
                "Define an enforced Content-Security-Policy appropriate for the application.");
        else if (csp is null) result.Set("headers.csp", CheckOutcome.NotTested, "unsupported_policy_syntax");
        else
        {
            var scripts = csp.GetValueOrDefault("script-src") ?? csp.GetValueOrDefault("default-src") ?? [];
            if (scripts.Contains("*") || scripts.Contains("'unsafe-eval'"))
                result.Fail("headers.csp", Severity.Low, "permissive_csp", "The effective script source list permits a wildcard or unsafe-eval.",
                    "Review the effective script source list and remove unnecessary permissions.");
            else result.Set("headers.csp", CheckOutcome.Passed);
        }
        Framing(page, csp, cspValues.Count > 0, result);
        if (page.Header("X-Content-Type-Options").Trim().Equals("nosniff", StringComparison.OrdinalIgnoreCase))
            result.Set("headers.nosniff", CheckOutcome.Passed);
        else result.Fail("headers.nosniff", Severity.Low, "missing_nosniff", "A valid nosniff header is missing.",
            "Set X-Content-Type-Options: nosniff.");
        Referrer(page, result);
        var permissions = page.Header("Permissions-Policy").Trim();
        if (permissions.Length == 0)
            result.Fail("headers.permissions", Severity.Low, "missing_permissions_policy", "Permissions-Policy is missing.",
                "Restrict browser features that this application does not need.");
        else
        {
            var features = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var valid = permissions.Split(',').All(part =>
            {
                var match = Regex.Match(part.Trim(), @"^([a-z][a-z0-9-]*)=(\*|\((self|""https?://[^""\s]+"")?(\s+(self|""https?://[^""\s]+""))*\))$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
                return match.Success && features.Add(match.Groups[1].Value);
            });
            result.Set("headers.permissions", valid ? CheckOutcome.Passed : CheckOutcome.NotTested,
                valid ? null : "unsupported_policy_syntax");
        }
    }
    private static void Hsts(HttpObservation page, CheckResults result)
    {
        const string id = "headers.hsts";
        if (page.Url.Scheme != "https") { result.Set(id, CheckOutcome.NotApplicable, "http_response"); return; }
        var values = page.Values("Strict-Transport-Security");
        if (values.Count > 1) { result.Set(id, CheckOutcome.NotTested, "ambiguous_header"); return; }
        var directives = page.Header("Strict-Transport-Security").Split(';', StringSplitOptions.TrimEntries);
        var ages = directives.Where(d => d.Split('=')[0].Trim().Equals("max-age", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (ages.Length == 1 && long.TryParse(ages[0].Split('=', 2).ElementAtOrDefault(1)?.Trim().Trim('"'),
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var age) && age > 0)
            result.Set(id, CheckOutcome.Passed);
        else result.Fail(id, Severity.Medium, "missing_hsts", "A positive valid HSTS max-age is missing.",
            "Configure a positive Strict-Transport-Security max-age after validating HTTPS deployment.");
    }
    private static Dictionary<string, string[]>? ParseCsp(string value)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "default-src", "script-src", "script-src-elem", "script-src-attr", "style-src", "style-src-elem", "style-src-attr",
            "img-src", "font-src", "connect-src", "media-src", "object-src", "frame-src", "child-src", "worker-src", "manifest-src",
            "base-uri", "form-action", "frame-ancestors", "sandbox", "report-uri", "report-to", "upgrade-insecure-requests",
            "block-all-mixed-content", "require-trusted-types-for", "trusted-types"
        };
        var policy = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tokens = part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || !known.Contains(tokens[0]) || !Regex.IsMatch(tokens[0], "^[a-zA-Z][a-zA-Z0-9-]*$", RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(50)) || !policy.TryAdd(tokens[0], tokens[1..]) ||
                tokens.Skip(1).Any(t => t.Contains(',') || t.Contains('"') || t.Any(char.IsControl) ||
                    (t.StartsWith('\'') != t.EndsWith('\'')))) return null;
        }
        return policy.Count == 0 ? null : policy;
    }
    private static void Framing(HttpObservation page, Dictionary<string, string[]>? csp, bool hasCsp, CheckResults result)
    {
        const string id = "headers.framing";
        if (csp is not null && csp.TryGetValue("frame-ancestors", out var ancestors))
        {
            if (ancestors.Any(source => !ValidFrameSource(source)))
            { result.Set(id, CheckOutcome.NotTested, "unsupported_policy_syntax"); return; }
            if (ancestors.Length == 0 || (ancestors.Contains("'none'") && ancestors.Length > 1))
                result.Set(id, CheckOutcome.NotTested, "unsupported_policy_syntax");
            else if (ancestors.Any(a => Regex.IsMatch(a, @"^(?:https?://)?\*(?::(?:[0-9]{1,5}|\*))?(?:/[^\s?#]*)?$",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)) || Regex.IsMatch(a, "^[a-zA-Z][a-zA-Z0-9+.-]*:$",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))))
                result.Fail(id, Severity.Medium, "permissive_framing", "CSP allows framing by unrestricted origins.",
                    "Restrict frame-ancestors to trusted origins, self, or none.");
            else result.Set(id, CheckOutcome.Passed);
            return;
        }
        if (hasCsp && csp is null) { result.Set(id, CheckOutcome.NotTested, "unsupported_policy_syntax"); return; }
        var xfo = page.Header("X-Frame-Options").Trim().ToUpperInvariant();
        if (xfo is "DENY" or "SAMEORIGIN") result.Set(id, CheckOutcome.Passed);
        else result.Fail(id, Severity.Medium, "missing_framing_policy", "No supported restrictive framing policy was observed.",
            "Use CSP frame-ancestors or a supported X-Frame-Options policy.");
    }
    private static bool ValidFrameSource(string source)
    {
        if (source is "'none'" or "'self'" or "*") return true;
        if (Regex.IsMatch(source, "^[a-zA-Z][a-zA-Z0-9+.-]*:$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))) return true;
        return Regex.IsMatch(source, @"^(?:https?://)?(?:\*|(?:\*\.)?[a-zA-Z0-9-]+(?:\.[a-zA-Z0-9-]+)*)(?::(?:[0-9]{1,5}|\*))?(?:/[^\s?#]*)?$",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    }
    private static void Referrer(HttpObservation page, CheckResults result)
    {
        var known = new[] { "no-referrer", "same-origin", "origin", "strict-origin", "origin-when-cross-origin",
            "strict-origin-when-cross-origin", "unsafe-url", "no-referrer-when-downgrade" };
        var selected = page.Header("Referrer-Policy").Split(',', StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant()).LastOrDefault(t => known.Contains(t));
        if (selected is null or "unsafe-url" or "no-referrer-when-downgrade")
            result.Fail("headers.referrer", Severity.Low, "unsafe_referrer_policy", "A supported conservative Referrer-Policy is missing.",
                "Choose a policy such as strict-origin-when-cross-origin appropriate for the application.");
        else result.Set("headers.referrer", CheckOutcome.Passed);
    }
}
