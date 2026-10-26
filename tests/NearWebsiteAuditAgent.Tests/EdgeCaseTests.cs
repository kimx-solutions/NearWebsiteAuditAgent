using System.Net;
using NearWebsiteAuditAgent.Contracts;
using NearWebsiteAuditAgent.Core;
using NearWebsiteAuditAgent.Infrastructure;

namespace NearWebsiteAuditAgent.Tests;

public class EdgeCaseTests
{
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Mutating_methods_can_never_reach_a_target(string method)
    {
        await using var server = new HttpFixture(_ => new());
        var options = new AuditOptions();
        using var budget = new AuditBudget(options);
        var result = await TransportTests.Transport(server, options)
            .FetchAsync(TransportTests.Target, new(method), budget, null, default);
        Assert.Equal("invalid_input", result.ErrorCode);
        Assert.Empty(server.Requests);
    }
    [Fact]
    public void An_empty_canonical_href_is_not_a_valid_canonical_declaration()
    {
        var html = new HtmlObservation(1, 1, 1, 1, 1, 1, [""], [], []);
        var result = new CheckEngine().Evaluate(CheckEngineTests.Observe(CheckEngineTests.Page(), html));
        Assert.Equal(CheckOutcome.Failed, Assert.Single(result.Checks, c => c.Id == "seo.canonical").Outcome);
    }
    [Fact]
    public async Task Oversized_successful_submit_responses_are_still_ambiguous_not_safe_to_replay()
    {
        var handler = new MarketClientTests.Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(new string(' ', 4194305)) }));
        var client = new MarketClient(new HttpClient(handler) { BaseAddress = new("https://market.near.ai/") },
            new() { AgentApiToken = "aat_FAKE" });
        var error = await Assert.ThrowsAsync<MarketException>(() => client.SubmitAsync(MarketClientTests.Id, "# result", default));
        Assert.True(error.OutcomeUnknown);
        Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task A_start_that_consumes_the_SLA_cannot_begin_an_audit()
    {
        using var s = new MarketWorkTests.Scenario();
        var clock = new AdjustableClock(s.Market.Current.SlaDeadlineAt.AddSeconds(-30));
        s.Market.AfterStart = () => clock.Now = s.Market.Current.SlaDeadlineAt.AddSeconds(1);
        var processor = new MarketWorkProcessor(s.Market, s.Repository, s.Runner, new ReportWriter(s.Store, new()),
            s.Store, s.Options, clock);
        var work = await processor.ProcessAsync(s.Market.Current, default);
        Assert.Equal(AssignmentState.Stopped, work!.State);
        Assert.Equal(0, s.Runner.Calls);
        Assert.Equal(0, s.Market.Submissions);
    }
    [Fact]
    public async Task Excessive_HTML_node_counts_are_bounded_before_DOM_allocation()
    {
        var error = await Assert.ThrowsAsync<AuditException>(() => new HtmlInspector()
            .ParseAsync(string.Concat(Enumerable.Repeat("<br>", 50001)), default));
        Assert.Equal("html_too_complex", error.Code);
    }
    [Fact]
    public async Task Missing_required_journal_fields_fail_closed()
    {
        using var directory = new TemporaryDirectory();
        var store = new AtomicFileStore(directory.Path);
        await store.WriteAsync("bad.work.json", """{"schemaVersion":1,"state":"running"}"""u8.ToArray(), false, default);
        var error = await Assert.ThrowsAsync<AuditException>(() => new FileAssignmentRepository(store).ReadAllAsync(default));
        Assert.Equal("storage_unavailable", error.Code);
    }
    private sealed class AdjustableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
