using System.Net;
using System.Text.Json;
using NearWebsiteAuditAgent.Contracts;
using NearWebsiteAuditAgent.Core;

namespace NearWebsiteAuditAgent.Tests;

public class FoundationTests
{
    [Theory]
    [InlineData("""{"url":"https://example.com/a?secret=value"}""")]
    [InlineData("""{"url":"http://example.com/"}""")]
    public void Strict_input_preserves_the_requested_resource(string json)
    {
        var request = AuditInput.Parse(json);
        Assert.Contains("example.com", request.Url);
    }

    [Theory]
    [InlineData("""{"url":"https://a.com","url":"https://b.com"}""")]
    [InlineData("""{"url":"https://a.com","headers":{}}""")]
    [InlineData("""{"url":42}""")]
    [InlineData("""{"url":""}""")]
    [InlineData("""["https://a.com"]""")]
    [InlineData("Please audit https://a.com")]
    [InlineData("""{"url":"https://a.com"} {}""")]
    public void Invalid_briefs_cannot_inject_options_or_multiple_targets(string json)
        => Assert.Throws<AuditException>(() => AuditInput.Parse(json));

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:password@example.com/")]
    [InlineData("https://example.com:8080/")]
    [InlineData("https://localhost/")]
    [InlineData("https://metadata.google.internal/")]
    [InlineData("https://example.com\\@127.0.0.1/")]
    [InlineData("https://[fe80::1%25eth0]/")]
    [InlineData("https://127.1/")]
    [InlineData("https://2130706433/")]
    public void Invalid_or_restricted_targets_are_rejected_before_transport(string url)
        => Assert.Throws<AuditException>(() => TargetUri.Parse(url));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.100.100.200")]
    [InlineData("198.18.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("168.63.129.16")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002:7f00:1::")]
    [InlineData("3fff::1")]
    public void Restricted_addresses_cannot_be_dialed(string address)
        => Assert.False(PublicAddressPolicy.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    public void Public_control_addresses_are_accepted(string address)
        => Assert.True(PublicAddressPolicy.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public void Scoring_deducts_the_highest_weight_once_per_check()
    {
        var checks = new[]
        {
            new CheckResult("cookies.secure", CheckOutcome.Failed),
            new CheckResult("seo.title", CheckOutcome.Failed)
        };
        var findings = new[]
        {
            new AuditFinding("cookie_a", "cookies.secure", "security", Severity.High, "d", [], "r"),
            new AuditFinding("cookie_b", "cookies.secure", "security", Severity.Medium, "d", [], "r"),
            new AuditFinding("title", "seo.title", "seo", Severity.Low, "d", [], "r")
        };
        var score = Scoring.Calculate(checks, findings);
        Assert.Equal(82, score.Score);
        Assert.Equal("B", score.Grade);
        Assert.False(score.Provisional);
    }

    [Fact]
    public void Untested_checks_cannot_produce_a_passing_grade()
    {
        var score = Scoring.Calculate([
            new("tls.https", CheckOutcome.Passed),
            new("seo.title", CheckOutcome.NotTested, ReasonCode: "network_failed")
        ], []);
        Assert.Equal(100, score.Score);
        Assert.Null(score.Grade);
        Assert.True(score.Provisional);
        Assert.Null(Scoring.Calculate([new("tls.https", CheckOutcome.NotTested)], []).Score);
    }

    [Fact]
    public void Report_enum_tokens_match_the_wire_contract()
    {
        var json = JsonSerializer.Serialize(new CheckResult("tls.https", CheckOutcome.NotTested), ReportJson.Options);
        Assert.Contains(@"""outcome"": ""notTested""", json);
        Assert.DoesNotContain("NotTested", json);
    }
}
