using System.Text.Json;
using NearWebsiteAuditAgent.Contracts;
using NearWebsiteAuditAgent.Core;
using NearWebsiteAuditAgent.Infrastructure;

namespace NearWebsiteAuditAgent.Tests;

public class CheckEngineTests
{
    private static readonly Uri Target = new("https://audit.example.com/");
    private readonly CheckEngine _engine = new();

    public static HttpObservation Page(Dictionary<string, string[]>? headers = null)
        => new(Target, 200, headers ?? new(StringComparer.OrdinalIgnoreCase), "", "text/html");
    public static AuditObservation Observe(HttpObservation page, HtmlObservation? html = null)
        => new() { Main = new(page, []), Html = html };
    private static CheckResult Check(AuditEvaluation result, string id) => Assert.Single(result.Checks, c => c.Id == id);

    [Fact]
    public void Missing_observations_never_become_missing_header_findings()
    {
        var result = _engine.Evaluate(new());
        Assert.Equal(22, result.Checks.Count);
        Assert.All(result.Checks, check => Assert.Equal(CheckOutcome.NotTested, check.Outcome));
        Assert.Empty(result.Findings);
    }

    [Theory]
    [InlineData("headers.hsts")]
    [InlineData("headers.csp")]
    [InlineData("headers.framing")]
    [InlineData("headers.nosniff")]
    [InlineData("headers.referrer")]
    [InlineData("headers.permissions")]
    public void Observed_missing_security_headers_are_actionable(string id)
    {
        var result = _engine.Evaluate(Observe(Page()));
        Assert.Equal(CheckOutcome.Failed, Check(result, id).Outcome);
        Assert.Contains(result.Findings, f => f.CheckId == id && f.Evidence.Count > 0 && f.Recommendation.Length > 0);
    }

    [Theory]
    [InlineData("Strict-Transport-Security", "max-age=31536000", "headers.hsts", CheckOutcome.Passed)]
    [InlineData("Strict-Transport-Security", "max-age=0", "headers.hsts", CheckOutcome.Failed)]
    [InlineData("Strict-Transport-Security", "max-age=broken", "headers.hsts", CheckOutcome.Failed)]
    [InlineData("Content-Security-Policy", "default-src 'self'; frame-ancestors 'none'", "headers.csp", CheckOutcome.Passed)]
    [InlineData("Content-Security-Policy-Report-Only", "default-src 'none'", "headers.csp", CheckOutcome.Failed)]
    [InlineData("Content-Security-Policy", "script-src 'unsafe-eval'", "headers.csp", CheckOutcome.Failed)]
    [InlineData("Content-Security-Policy", "script-src *", "headers.csp", CheckOutcome.Failed)]
    [InlineData("Content-Security-Policy", "script-src 'self'; script-src *", "headers.csp", CheckOutcome.NotTested)]
    [InlineData("Content-Security-Policy", "invented-directive whatever", "headers.csp", CheckOutcome.NotTested)]
    [InlineData("Content-Security-Policy", "frame-ancestors https://[", "headers.framing", CheckOutcome.NotTested)]
    [InlineData("Content-Security-Policy", "frame-ancestors https://*", "headers.framing", CheckOutcome.Failed)]
    [InlineData("Content-Security-Policy", "frame-ancestors http://*:8080", "headers.framing", CheckOutcome.Failed)]
    [InlineData("Content-Security-Policy", "frame-ancestors *:*", "headers.framing", CheckOutcome.Failed)]
    [InlineData("Content-Security-Policy", "frame-ancestors https://*.example.com", "headers.framing", CheckOutcome.Passed)]
    [InlineData("X-Frame-Options", "SAMEORIGIN", "headers.framing", CheckOutcome.Passed)]
    [InlineData("X-Frame-Options", "ALLOW-FROM https://example.com", "headers.framing", CheckOutcome.Failed)]
    [InlineData("X-Content-Type-Options", "NOSNIFF", "headers.nosniff", CheckOutcome.Passed)]
    [InlineData("Referrer-Policy", "unsafe-url, strict-origin-when-cross-origin", "headers.referrer", CheckOutcome.Passed)]
    [InlineData("Referrer-Policy", "unsafe-url", "headers.referrer", CheckOutcome.Failed)]
    [InlineData("Permissions-Policy", "camera=(), geolocation=(self)", "headers.permissions", CheckOutcome.Passed)]
    [InlineData("Permissions-Policy", "camera=*", "headers.permissions", CheckOutcome.Passed)]
    [InlineData("Permissions-Policy", "unparseable", "headers.permissions", CheckOutcome.NotTested)]
    public void Header_rules_parse_tokens_and_preserve_uncertainty(string name, string value, string id, CheckOutcome expected)
        => Assert.Equal(expected, Check(_engine.Evaluate(Observe(Page(new() { [name] = [value] }))), id).Outcome);

    [Fact]
    public void Permissive_CSP_framing_cannot_be_rescued_by_XFO()
    {
        var result = _engine.Evaluate(Observe(Page(new()
        {
            ["Content-Security-Policy"] = ["frame-ancestors *"],
            ["X-Frame-Options"] = ["DENY"]
        })));
        Assert.Equal(CheckOutcome.Failed, Check(result, "headers.framing").Outcome);
    }

    [Theory]
    [InlineData("sid=COOKIE_SECRET; Secure; HttpOnly; SameSite=Lax", CheckOutcome.Passed, CheckOutcome.Passed, CheckOutcome.Passed)]
    [InlineData("sid=COOKIE_SECRET; SameSite=None", CheckOutcome.Failed, CheckOutcome.NotTested, CheckOutcome.Failed)]
    [InlineData("sid=COOKIE_SECRET; Expires=Wed, 09 Jun 2038 10:18:14 GMT; Secure; SameSite=Strict", CheckOutcome.Passed, CheckOutcome.NotTested, CheckOutcome.Passed)]
    public void Cookies_are_parsed_per_header_without_disclosing_values(string cookie,
        CheckOutcome secure, CheckOutcome httpOnly, CheckOutcome sameSite)
    {
        var result = _engine.Evaluate(Observe(Page(new() { ["Set-Cookie"] = [cookie] })));
        Assert.Equal(secure, Check(result, "cookies.secure").Outcome);
        Assert.Equal(httpOnly, Check(result, "cookies.httponly").Outcome);
        Assert.Equal(sameSite, Check(result, "cookies.samesite").Outcome);
        Assert.DoesNotContain("COOKIE_SECRET", JsonSerializer.Serialize(result, ReportJson.Options));
    }

    [Fact]
    public void Cookie_lifetime_and_flags_are_observable_in_both_report_formats_without_values()
    {
        var result = _engine.Evaluate(Observe(Page(new()
        {
            ["Set-Cookie"] = [
                "session=COOKIE_SECRET; Secure; HttpOnly; SameSite=Lax",
                "saved=COOKIE_SECRET; Max-Age=3600; Secure; HttpOnly; SameSite=Lax",
                "dated=COOKIE_SECRET; Expires=Wed, 09 Jun 2038 10:18:14 GMT; Secure; HttpOnly; SameSite=Strict"]
        })));
        var evidence = Check(result, "cookies.secure").Evidence;
        Assert.Contains(evidence, e => e.Subject == "session" && e.Detail.Contains("lifetime=nonpersistent"));
        Assert.Contains(evidence, e => e.Subject == "saved" && e.Detail.Contains("lifetime=persistent"));
        Assert.Contains(evidence, e => e.Subject == "dated" && e.Detail.Contains("lifetime=persistent"));
        var report = new AuditResult { Checks = result.Checks, Findings = result.Findings };
        var markdown = ReportWriter.Markdown(report);
        Assert.Contains("lifetime=persistent", markdown);
        Assert.DoesNotContain("COOKIE_SECRET", markdown + JsonSerializer.Serialize(report, ReportJson.Options));
    }

    [Fact]
    public void Cookie_evidence_is_bounded_and_malformed_expiry_does_not_become_session_inference()
    {
        var result = _engine.Evaluate(Observe(Page(new()
        {
            ["Set-Cookie"] = Enumerable.Range(0, 20).Select(i =>
                $"c{i:D2}=SECRET; Max-Age=invalid; Secure; HttpOnly; SameSite=Lax").ToArray()
        })));
        var evidence = Check(result, "cookies.secure").Evidence;
        Assert.Equal(4, evidence.Count);
        Assert.Contains(evidence, e => e.Detail.Contains("lifetime=unknown"));
        Assert.Contains(evidence, e => e.Kind == "summary");
    }

    [Fact]
    public void No_cookie_is_inapplicable_only_when_a_response_exists()
    {
        var result = _engine.Evaluate(Observe(Page()));
        Assert.All(result.Checks.Where(c => c.Id.StartsWith("cookies.", StringComparison.Ordinal)),
            c => Assert.Equal(CheckOutcome.NotApplicable, c.Outcome));
    }

    [Fact]
    public void Invalid_certificate_evidence_is_not_lost_when_HTML_cannot_be_read()
    {
        var result = _engine.Evaluate(new()
        {
            Main = new(null, [], new(true, false, true, false, false, false, null), "tls_validation_failed")
        });
        Assert.Equal(CheckOutcome.Failed, Check(result, "tls.certificate").Outcome);
        Assert.Equal(CheckOutcome.NotTested, Check(result, "tls.protocol").Outcome);
        Assert.Equal(CheckOutcome.NotTested, Check(result, "seo.title").Outcome);
    }

    [Theory]
    [InlineData("Tls12")]
    [InlineData("Tls13")]
    public void A_valid_negotiated_TLS_version_is_reported(string protocol)
    {
        var result = _engine.Evaluate(new()
        {
            Main = new(Page(), [], new(true, true, false, false, true, true, protocol))
        });
        Assert.Equal(CheckOutcome.Passed, Check(result, "tls.protocol").Outcome);
        Assert.Equal(CheckOutcome.Passed, Check(result, "tls.https").Outcome);
    }

    [Fact]
    public async Task HTML_extraction_does_not_execute_script_or_fetch_resources()
    {
        var html = await new HtmlInspector().ParseAsync("""
            <title>audit</title><meta name="description" content="description"><h1>one</h1>
            <link rel="canonical" href="/relative"><meta name="robots" content="noindex,nofollow">
            <a href="/one#fragment">one</a><a href="javascript:alert(1)">script</a>
            <script src="http://127.0.0.1/secret">throw new Error('never run')</script>
            """, default);
        var result = _engine.Evaluate(Observe(Page(), html));
        foreach (var id in new[] { "seo.title", "seo.description", "seo.h1", "seo.canonical", "seo.robots_meta" })
            Assert.Equal(CheckOutcome.Passed, Check(result, id).Outcome);
        Assert.Contains(result.Findings, f => f.Severity == Severity.Info && f.CheckId == "seo.robots_meta");
        Assert.Equal(2, html.Hrefs.Count);
    }

    [Theory]
    [InlineData("seo.title")]
    [InlineData("seo.description")]
    [InlineData("seo.h1")]
    [InlineData("seo.canonical")]
    public void Missing_DOM_elements_fail_only_after_HTML_was_observed(string id)
    {
        var html = new HtmlObservation(0, 0, 0, 0, 0, 0, [], [], []);
        Assert.Equal(CheckOutcome.Failed, Check(_engine.Evaluate(Observe(Page(), html)), id).Outcome);
    }

    [Theory]
    [InlineData(404, "", CheckOutcome.Failed)]
    [InlineData(403, "", CheckOutcome.NotTested)]
    [InlineData(200, "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\"/>", CheckOutcome.Passed)]
    [InlineData(200, "<html/>", CheckOutcome.Failed)]
    [InlineData(200, "<!DOCTYPE x [<!ENTITY e SYSTEM 'http://127.0.0.1/'>]><urlset>&e;</urlset>", CheckOutcome.Failed)]
    public void Sitemap_checks_distinguish_absence_from_unavailability_and_disable_entities(int status, string xml, CheckOutcome expected)
    {
        var result = _engine.Evaluate(new()
        {
            Main = new(Page(), []),
            Sitemap = new(new(new Uri(Target, "/sitemap.xml"), status, new Dictionary<string, string[]>(), xml, "application/xml"), [])
        });
        Assert.Equal(expected, Check(result, "seo.sitemap").Outcome);
    }

    [Fact]
    public void Sitemap_within_supported_response_budget_is_not_misclassified_as_invalid_XML()
    {
        var xml = "<urlset><!--" + new string('x', 2097152) + "--></urlset>";
        var result = _engine.Evaluate(new()
        {
            Main = new(Page(), []),
            Sitemap = new(new(new Uri(Target, "/sitemap.xml"), 200, new Dictionary<string, string[]>(), xml, "application/xml"), [])
        });
        Assert.Equal(CheckOutcome.Passed, Check(result, "seo.sitemap").Outcome);
        Assert.DoesNotContain(result.Findings, f => f.Id == "invalid_sitemap");
    }

    [Fact]
    public void Broken_links_and_unknown_links_keep_both_failure_and_incomplete_coverage()
    {
        var broken = new Uri(Target, "/broken");
        var busy = new Uri(Target, "/busy");
        var result = _engine.Evaluate(new()
        {
            Main = new(Page(), []),
            Scope = new() { LinksEligible = 2, LinksSelected = 2, LinksAttempted = 2, LinksInconclusive = 1 },
            Links =
            [
                new(broken, new(new(broken, 404, new Dictionary<string, string[]>()), [])),
                new(busy, new(new(busy, 503, new Dictionary<string, string[]>()), []))
            ]
        });
        var check = Check(result, "seo.links");
        Assert.Equal(CheckOutcome.Failed, check.Outcome);
        Assert.Equal(Completeness.Partial, check.Completeness);
        Assert.True(Scoring.Calculate(result.Checks, result.Findings).Provisional);
    }
}
