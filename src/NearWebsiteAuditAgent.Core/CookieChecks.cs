using NearWebsiteAuditAgent.Contracts;

namespace NearWebsiteAuditAgent.Core;

internal static class CookieChecks
{
    private sealed record Cookie(string Name, bool Secure, bool HttpOnly, string? SameSite, string Lifetime);
    public static void Evaluate(HttpObservation? page, CheckResults result)
    {
        if (page is null) return;
        var headers = page.Values("Set-Cookie");
        var ids = new[] { "cookies.secure", "cookies.httponly", "cookies.samesite" };
        if (headers.Count == 0)
        {
            foreach (var id in ids) result.Set(id, CheckOutcome.NotApplicable, "no_cookies");
            return;
        }
        var cookies = headers.Select(Parse).ToArray();
        if (cookies.Any(c => c is null))
        {
            foreach (var id in ids) result.Set(id, CheckOutcome.NotTested, "malformed_cookie");
            return;
        }
        foreach (var id in ids) result.Set(id, CheckOutcome.Passed);
        foreach (var cookie in cookies.OfType<Cookie>())
        {
            var sameSite = new[] { "Lax", "Strict", "None" }.FirstOrDefault(s => s.Equals(cookie.SameSite, StringComparison.OrdinalIgnoreCase)) ?? "unknown";
            foreach (var id in ids) result.Evidence(id, new("cookie", cookie.Name,
                $"Secure={cookie.Secure}; HttpOnly={cookie.HttpOnly}; SameSite={sameSite}; lifetime={cookie.Lifetime}; purpose=unknown"));
            if (!cookie.Secure)
                result.Fail("cookies.secure", Severity.Medium, "cookie_missing_secure", "Cookie is missing Secure.",
                    "Serve the cookie over HTTPS and set Secure.", cookie.Name);
            if (!cookie.HttpOnly)
            {
                result.Set("cookies.httponly", CheckOutcome.NotTested, "cookie_purpose_unknown");
                result.Finding("cookies.httponly", Severity.Info, "cookie_httponly_unknown",
                    "HttpOnly is absent; authentication applicability is unknown.",
                    "Determine whether script access is required before setting HttpOnly.", cookie.Name);
            }
            if (cookie.SameSite?.Equals("None", StringComparison.OrdinalIgnoreCase) == true && !cookie.Secure)
                result.Fail("cookies.samesite", Severity.High, "cookie_none_without_secure", "SameSite=None is present without Secure.",
                    "Set Secure for SameSite=None cookies and use HTTPS.", cookie.Name);
            else if (cookie.SameSite is null || !new[] { "Lax", "Strict", "None" }.Contains(cookie.SameSite, StringComparer.OrdinalIgnoreCase))
                result.Fail("cookies.samesite", Severity.Low, "cookie_missing_samesite", "A supported SameSite attribute is missing.",
                    "Choose Lax, Strict, or None with Secure according to the cookie purpose.", cookie.Name);
        }
    }
    private static Cookie? Parse(string header)
    {
        var parts = header.Split(';', StringSplitOptions.TrimEntries);
        var pair = parts[0].Split('=', 2);
        if (pair.Length != 2 || pair[0].Length == 0 || pair[0].Any(c => c <= 32 || c >= 127 || "()<>@,;:\\\"/[]?={}".Contains(c)) ||
            pair[1].Contains(',')) return null;
        var attributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts.Skip(1).Where(p => p.Length > 0))
        {
            var attribute = part.Split('=', 2);
            if (!attributes.TryAdd(attribute[0].Trim(), attribute.ElementAtOrDefault(1)?.Trim())) return null;
        }
        var safeName = Redaction.Token(pair[0]);
        return new(safeName.Length == 0 ? "cookie" : safeName, attributes.ContainsKey("Secure"),
            attributes.ContainsKey("HttpOnly"), attributes.GetValueOrDefault("SameSite"), Lifetime(attributes));
    }
    private static string Lifetime(Dictionary<string, string?> attributes)
    {
        if (attributes.TryGetValue("Max-Age", out var age) && age is not null &&
            System.Text.RegularExpressions.Regex.IsMatch(age, @"^-?[0-9]+$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))) return "persistent";
        if (attributes.TryGetValue("Expires", out var expires) && expires is not null &&
            DateTimeOffset.TryParse(expires[(expires.IndexOf(',') + 1)..], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out _)) return "persistent";
        return attributes.ContainsKey("Max-Age") || attributes.ContainsKey("Expires") ? "unknown" : "nonpersistent";
    }
}
