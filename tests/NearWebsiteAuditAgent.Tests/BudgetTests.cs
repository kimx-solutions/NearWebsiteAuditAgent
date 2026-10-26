using NearWebsiteAuditAgent.Core;

namespace NearWebsiteAuditAgent.Tests;

public class BudgetTests
{
    [Fact]
    public async Task Concurrent_transport_requests_obey_the_same_audit_semaphore()
    {
        var current = 0;
        var peak = 0;
        await using var server = new HttpFixture(async (_, ct) =>
        {
            var active = Interlocked.Increment(ref current);
            Interlocked.Exchange(ref peak, Math.Max(peak, active));
            try { await Task.Delay(50, ct); return new FixtureResponse(); }
            finally { Interlocked.Decrement(ref current); }
        });
        var options = new AuditOptions { MaxConcurrentRequestsPerAudit = 1 };
        using var budget = new AuditBudget(options);
        var transport = TransportTests.Transport(server, options);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            transport.FetchAsync(TransportTests.Target, HttpMethod.Get, budget, null, default)));
        Assert.Equal(1, peak);
        Assert.Equal(4, server.Requests.Count);
    }

    [Fact]
    public async Task Total_decoded_body_budget_is_shared_between_responses()
    {
        await using var server = new HttpFixture(_ => new(200, new string('a', 80)));
        var options = new AuditOptions { MaxResponseBytes = 80, MaxTotalResponseBytes = 100 };
        using var budget = new AuditBudget(options);
        var transport = TransportTests.Transport(server, options);
        Assert.Null((await transport.FetchAsync(TransportTests.Target, HttpMethod.Get, budget, null, default)).ErrorCode);
        var result = await transport.FetchAsync(TransportTests.Target, HttpMethod.Get, budget, null, default);
        Assert.Equal("budget_exceeded", result.ErrorCode);
        Assert.Contains("max_total_response_bytes", budget.Snapshot().LimitsReached);
    }

    [Fact]
    public void Repeated_findings_keep_bounded_evidence_without_changing_the_score()
    {
        var cookies = Enumerable.Range(0, 1000).Select(i => "cookie_" + i + "=SECRET; SameSite=None").ToArray();
        var observation = CheckEngineTests.Observe(CheckEngineTests.Page(new() { ["Set-Cookie"] = cookies }));
        var result = new CheckEngine().Evaluate(observation);
        Assert.True(result.Findings.Count < 30);
        Assert.All(result.Findings, f => Assert.True(f.Evidence.Count <= 4));
        Assert.Equal(77, Scoring.Calculate(result.Checks.Where(c => c.Id.StartsWith("cookies.", StringComparison.Ordinal)).ToArray(),
            result.Findings.Where(f => f.CheckId.StartsWith("cookies.", StringComparison.Ordinal)).ToArray()).Score); // Medium 8 + High 15
    }
}
