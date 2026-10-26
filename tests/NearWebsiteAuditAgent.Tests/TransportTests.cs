using System.Net;
using NearWebsiteAuditAgent.Core;
using NearWebsiteAuditAgent.Infrastructure;

namespace NearWebsiteAuditAgent.Tests;

public class TransportTests
{
    internal static readonly IPAddress PublicIp = IPAddress.Parse("93.184.216.34");
    internal static readonly Uri Target = new("http://audit.example.com/");
    internal static SafeTargetTransport Transport(HttpFixture server, AuditOptions options,
        FixtureResolver? resolver = null, FixtureConnector? connector = null)
        => new(options, resolver ?? new(_ => [PublicIp]), connector ?? new(server.Port));

    [Fact]
    public async Task Mixed_DNS_answers_are_rejected_without_opening_a_socket()
    {
        await using var server = new HttpFixture(_ => new());
        var connector = new FixtureConnector(server.Port);
        var options = new AuditOptions();
        using var budget = new AuditBudget(options);
        var result = await Transport(server, options, new(_ => [PublicIp, IPAddress.Loopback]), connector)
            .FetchAsync(Target, HttpMethod.Get, budget, null, default);
        Assert.Equal("target_restricted", result.ErrorCode);
        Assert.Equal(0, connector.Calls);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task A_connected_peer_must_match_the_vetted_address_before_HTTP_bytes()
    {
        await using var server = new HttpFixture(_ => new());
        var options = new AuditOptions();
        using var budget = new AuditBudget(options);
        var result = await Transport(server, options, connector: new(server.Port, IPAddress.Loopback))
            .FetchAsync(Target, HttpMethod.Get, budget, null, default);
        Assert.Equal("target_restricted", result.ErrorCode);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task DNS_rebinding_on_a_redirect_is_revalidated()
    {
        await using var server = new HttpFixture(_ => new(302, Headers: new Dictionary<string, string> { ["Location"] = "/next" }));
        var options = new AuditOptions();
        var calls = 0;
        using var budget = new AuditBudget(options);
        var result = await Transport(server, options, new(_ => ++calls == 1 ? [PublicIp] : [IPAddress.Loopback]))
            .FetchAsync(Target, HttpMethod.Get, budget, null, default);
        Assert.Equal("target_restricted", result.ErrorCode);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task A_redirect_to_metadata_is_stopped_before_the_next_connection()
    {
        await using var server = new HttpFixture(_ => new(302, Headers: new Dictionary<string, string>
        { ["Location"] = "http://169.254.169.254/latest/meta-data/" }));
        var options = new AuditOptions();
        var connector = new FixtureConnector(server.Port);
        using var budget = new AuditBudget(options);
        var result = await Transport(server, options, connector: connector)
            .FetchAsync(Target, HttpMethod.Get, budget, null, default);
        Assert.Equal("target_restricted", result.ErrorCode);
        Assert.Equal(1, connector.Calls);
    }

    [Fact]
    public async Task Relative_redirects_preserve_method_and_revalidate_each_socket()
    {
        await using var server = new HttpFixture(request => request.Path == "/"
            ? new(302, Headers: new Dictionary<string, string> { ["Location"] = "/final" })
            : new(200, "final"));
        var resolver = new FixtureResolver(_ => [PublicIp]);
        var options = new AuditOptions();
        using var budget = new AuditBudget(options);
        var result = await Transport(server, options, resolver).FetchAsync(Target, HttpMethod.Get, budget, null, default);
        Assert.Null(result.ErrorCode);
        Assert.Equal("/final", result.Response!.Url.AbsolutePath);
        Assert.Equal("final", result.Response.Body);
        Assert.Equal(2, resolver.Calls);
        Assert.All(server.Requests, r => Assert.DoesNotContain("Authorization", r.Headers, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Redirect_loops_and_same_origin_limits_do_not_follow_unboundedly()
    {
        await using var server = new HttpFixture(_ => new(302, Headers: new Dictionary<string, string> { ["Location"] = "/" }));
        var options = new AuditOptions();
        using var budget = new AuditBudget(options);
        var result = await Transport(server, options).FetchAsync(Target, HttpMethod.Get, budget, null, default);
        Assert.Equal("redirect_loop", result.ErrorCode);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Link_probes_do_not_follow_a_cross_origin_redirect()
    {
        await using var server = new HttpFixture(_ => new(302, Headers: new Dictionary<string, string>
        { ["Location"] = "http://other.example.com/" }));
        var options = new AuditOptions();
        using var budget = new AuditBudget(options);
        var result = await Transport(server, options).FetchAsync(Target, HttpMethod.Head, budget, Target, default);
        Assert.Equal("out_of_scope_redirect", result.ErrorCode);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Chunked_bodies_are_bounded_without_truncated_HTML_being_accepted()
    {
        await using var server = new HttpFixture(_ => new(200, new string('x', 1000), Chunked: true));
        var options = new AuditOptions { MaxResponseBytes = 128 };
        using var budget = new AuditBudget(options);
        var result = await Transport(server, options).FetchAsync(Target, HttpMethod.Get, budget, null, default);
        Assert.Equal("budget_exceeded", result.ErrorCode);
        Assert.Null(result.Response);
        Assert.Contains("max_response_bytes", budget.Snapshot().LimitsReached);
    }

    [Fact]
    public async Task Request_budget_is_shared_across_resources()
    {
        await using var server = new HttpFixture(_ => new());
        var options = new AuditOptions { MaxTargetAttempts = 1 };
        using var budget = new AuditBudget(options);
        var transport = Transport(server, options);
        Assert.Null((await transport.FetchAsync(Target, HttpMethod.Get, budget, null, default)).ErrorCode);
        Assert.Equal("budget_exceeded", (await transport.FetchAsync(new(Target, "/robots.txt"), HttpMethod.Get, budget, null, default)).ErrorCode);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task A_caller_cancellation_stops_inflight_HTTP_and_does_not_become_a_retry()
    {
        await using var server = new HttpFixture(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new FixtureResponse();
        });
        var options = new AuditOptions();
        using var budget = new AuditBudget(options);
        using var cancellation = new CancellationTokenSource(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Transport(server, options).FetchAsync(Target, HttpMethod.Get, budget, null, cancellation.Token));
        Assert.True(server.Requests.Count <= 1);
    }
}
