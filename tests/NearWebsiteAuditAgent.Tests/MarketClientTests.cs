using System.Net;
using System.Text;
using System.Text.Json;
using NearWebsiteAuditAgent.Core;
using NearWebsiteAuditAgent.Infrastructure;

namespace NearWebsiteAuditAgent.Tests;

public class MarketClientTests
{
    [Fact]
    public async Task A_timezone_free_platform_timestamp_is_not_a_valid_SLA_signal()
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"startedAt\":\"2026-10-26 12:00:00\"}") }));
        var error = await Assert.ThrowsAsync<MarketException>(() => Client(handler).StartAsync(Id, default));
        Assert.Equal("market_contract_changed", error.Code);
    }
    internal static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid JobId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    internal static readonly Guid AgentId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    internal static object Assignment(string status = "in_progress") => new
    {
        assignmentId = Id,
        jobId = JobId,
        workerAgentId = AgentId,
        status,
        escrowAmount = "1.0",
        escrowToken = "USD",
        slaSeconds = 600,
        slaDeadlineAt = DateTimeOffset.UtcNow.AddMinutes(10),
        acceptanceWindowDays = 7,
        createdAt = DateTimeOffset.UtcNow,
        updatedAt = DateTimeOffset.UtcNow,
        startedAt = (DateTimeOffset?)null,
        submittedAt = (DateTimeOffset?)null
    };
    internal static string Envelope() => JsonSerializer.Serialize(new
    {
        assignments = new[] { new { assignment = Assignment(), job = new
        {
            jobId = JobId, title = "Audit fixture", description = """{"url":"https://example.com/"}""",
            tags = Array.Empty<string>(), jobKind = "standard", status = "in_progress", attachments = Array.Empty<object>()
        }, latestMessage = (object?)null } }
    });
    internal sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request); }
    }
    private static MarketClient Client(Handler handler)
        => new(new HttpClient(handler) { BaseAddress = new("https://market.near.ai/") },
            new() { AgentApiToken = "aat_TEST_SECRET" });

    [Fact]
    public async Task Official_worker_envelope_is_parsed_and_the_token_stays_on_the_market_origin()
    {
        var handler = new Handler(request =>
        {
            Assert.Equal("market.near.ai", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("aat_TEST_SECRET", request.Headers.Authorization.Parameter);
            Assert.Contains("status=in_progress", request.RequestUri.Query);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Envelope()) });
        });
        var assignments = await Client(handler).PollAsync(default);
        Assert.Equal(Id, Assert.Single(assignments).AssignmentId);
        Assert.Equal("""{"url":"https://example.com/"}""", assignments[0].Brief);
    }

    [Fact]
    public async Task Empty_work_is_distinct_from_authentication_failure()
    {
        Assert.Empty(await Client(new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("""{"assignments":[]}""") }))).PollAsync(default));
        var unauthorized = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var error = await Assert.ThrowsAsync<MarketException>(() => Client(unauthorized).PollAsync(default));
        Assert.Equal("market_auth_failed", error.Code);
        Assert.True(error.PauseProcessing);
        Assert.Equal(1, unauthorized.Calls);
    }

    [Theory]
    [InlineData("""{"assignments":[{"assignment":{},"job":{}}]}""")]
    [InlineData("""{"assignments":"wrong"}""")]
    [InlineData("""{"unexpected":[]}""")]
    public async Task Malformed_required_contracts_cannot_be_treated_as_empty_work(string json)
    {
        var error = await Assert.ThrowsAsync<MarketException>(() => Client(new(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }))).PollAsync(default));
        Assert.Equal("market_contract_changed", error.Code);
    }

    [Fact]
    public async Task Read_transient_failures_are_bounded_and_retry_after_zero_is_respected()
    {
        var calls = 0;
        var handler = new Handler(_ =>
        {
            calls++;
            var response = new HttpResponseMessage(calls < 3 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
            response.Headers.RetryAfter = new(TimeSpan.Zero);
            response.Content = new StringContent("""{"assignments":[]}""");
            return Task.FromResult(response);
        });
        Assert.Empty(await Client(handler).PollAsync(default));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task A_submit_transport_failure_is_uncertain_and_is_never_blindly_retried()
    {
        var handler = new Handler(_ => throw new HttpRequestException("SIMULATED_RESPONSE_LOSS"));
        var error = await Assert.ThrowsAsync<MarketException>(() => Client(handler).SubmitAsync(Id, "# Audit", default));
        Assert.True(error.OutcomeUnknown);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("SIMULATED_RESPONSE_LOSS", error.Message);
    }

    [Fact]
    public async Task Start_and_submit_use_exact_official_worker_bodies()
    {
        var handler = new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            if (request.RequestUri!.AbsolutePath.EndsWith("/start", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"startedAt":"2026-10-26T12:00:00Z"}""") };
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("# Result", body.RootElement.GetProperty("deliverableMarkdown").GetString());
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(Assignment("submitted"))) };
        });
        var client = Client(handler);
        Assert.Equal(2026, (await client.StartAsync(Id, default)).Year);
        await client.SubmitAsync(Id, "# Result", default);
        Assert.Equal(2, handler.Calls);
    }
}
