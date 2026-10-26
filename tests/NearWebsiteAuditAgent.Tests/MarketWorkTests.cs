using NearWebsiteAuditAgent.Contracts;
using NearWebsiteAuditAgent.Core;
using NearWebsiteAuditAgent.Infrastructure;

namespace NearWebsiteAuditAgent.Tests;

public class MarketWorkTests
{
    private static MarketAssignment Assignment() => new(MarketClientTests.Id, MarketClientTests.JobId,
        MarketClientTests.AgentId, "in_progress", DateTimeOffset.UtcNow.AddMinutes(10), DateTimeOffset.UtcNow,
        """{"url":"https://example.com/"}""");

    internal sealed class Scenario : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        public readonly List<string> Events = [];
        public readonly AtomicFileStore Store;
        public readonly FileAssignmentRepository Repository;
        public readonly MarketOptions Options = new() { TestAssignmentIds = [MarketClientTests.Id] };
        public readonly FakeMarket Market;
        public readonly FakeRunner Runner;
        public readonly MarketWorkProcessor Processor;
        public Scenario(long maxBytes = 104857600)
        {
            Store = new(_directory.Path, maxBytes);
            Repository = new(Store);
            Market = new(Assignment(), Events);
            Runner = new(Events);
            Processor = new(Market, Repository, Runner, new ReportWriter(Store, new()), Store, Options);
        }
        public void Dispose() => _directory.Dispose();
    }
    internal sealed class FakeRunner(List<string> events) : IAuditRunner
    {
        public int Calls { get; private set; }
        public Action<CancellationToken>? OnRun { get; set; }
        public Task<AuditResult> RunAsync(AuditRequest request, CancellationToken cancellationToken, Guid? auditId = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            events.Add("audit");
            OnRun?.Invoke(cancellationToken);
            return Task.FromResult(new AuditResult
            {
                AuditId = auditId ?? Guid.NewGuid(),
                Url = "https://example.com/",
                Status = AuditStatus.Partial,
                Score = 100,
                ScoreProvisional = true,
                Checks = CheckCatalog.Ids.Select(id => new CheckResult(id, id == "tls.https" ? CheckOutcome.Passed : CheckOutcome.NotTested)).ToArray(),
                Coverage = new(22, 1, 0, 21, 0, 1)
            });
        }
    }
    internal sealed class FakeMarket(MarketAssignment assignment, List<string> events) : IMarketClient
    {
        public MarketAssignment Current { get; set; } = assignment;
        public int Submissions { get; private set; }
        public bool LoseSubmitResponse { get; set; }
        public Action? AfterStart { get; set; }
        public Action? AfterSubmit { get; set; }
        public Task<IReadOnlyList<MarketAssignment>> PollAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MarketAssignment>>([Current]);
        public Task<MarketAssignment?> FindAsync(Guid assignmentId, CancellationToken cancellationToken)
            => Task.FromResult<MarketAssignment?>(Current);
        public Task<DateTimeOffset> StartAsync(Guid assignmentId, CancellationToken cancellationToken)
        {
            events.Add("start");
            Current = Current with { StartedAt = DateTimeOffset.UtcNow };
            AfterStart?.Invoke();
            return Task.FromResult(Current.StartedAt.Value);
        }
        public Task SubmitAsync(Guid assignmentId, string markdown, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("submit");
            Submissions++;
            Current = Current with { Status = "submitted", SubmittedAt = DateTimeOffset.UtcNow };
            if (LoseSubmitResponse) throw new MarketException("submission_outcome_unknown", outcomeUnknown: true);
            Assert.Contains("Website audit", markdown);
            AfterSubmit?.Invoke();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Insufficient_report_headroom_prevents_platform_start_even_when_a_small_report_would_fit()
    {
        using var s = new Scenario(500000);
        await s.Store.WriteAsync("existing.json", new byte[100000], false, default);
        var error = await Assert.ThrowsAsync<AuditException>(() => s.Processor.ProcessAsync(s.Market.Current, default));
        Assert.Equal("storage_unavailable", error.Code);
        Assert.Empty(s.Events);
        Assert.Equal(AssignmentState.Validated, Assert.Single(await s.Repository.ReadAllAsync(default)).State);
        s.Store.Remove("existing.json");
        Assert.Equal(AssignmentState.Submitted, (await s.Processor.ProcessAsync(s.Market.Current, default))!.State);
    }

    [Fact]
    public async Task Insufficient_file_slots_prevent_platform_start()
    {
        using var s = new Scenario();
        for (var i = 0; i < 4090; i++) await File.WriteAllBytesAsync(Path.Combine(s.Store.Root, $"existing-{i}.json"), []);
        await Assert.ThrowsAsync<AuditException>(() => s.Processor.ProcessAsync(s.Market.Current, default));
        Assert.Empty(s.Events);
    }

    [Fact]
    public async Task Assignment_lifecycle_is_durable_and_duplicate_polls_do_not_resubmit()
    {
        using var s = new Scenario();
        var work = await s.Processor.ProcessAsync(s.Market.Current, default);
        Assert.Equal(AssignmentState.Submitted, work!.State);
        Assert.Equal(new[] { "start", "audit", "submit" }, s.Events);
        Assert.Equal(AssignmentState.Submitted, Assert.Single(await s.Repository.ReadAllAsync(default)).State);
        await s.Processor.ProcessAsync(s.Market.Current, default);
        Assert.Equal(1, s.Runner.Calls);
        Assert.Equal(1, s.Market.Submissions);
    }

    [Fact]
    public async Task Lost_submit_response_survives_restart_and_is_reconciled_without_resending()
    {
        using var s = new Scenario();
        s.Market.LoseSubmitResponse = true;
        var pending = await s.Processor.ProcessAsync(s.Market.Current, default);
        Assert.Equal(AssignmentState.AwaitingReconciliation, pending!.State);
        var restarted = new MarketWorkProcessor(s.Market, new FileAssignmentRepository(s.Store), s.Runner,
            new ReportWriter(s.Store, new()), s.Store, s.Options);
        Assert.Equal(AssignmentState.Submitted, (await restarted.ProcessAsync(s.Market.Current, default))!.State);
        Assert.Equal(1, s.Market.Submissions);
        Assert.Equal(1, s.Runner.Calls);
    }

    [Fact]
    public async Task An_unknown_inflight_submission_cannot_be_replayed_after_a_crash()
    {
        using var s = new Scenario();
        var assignment = s.Market.Current;
        await s.Repository.SaveAsync(new()
        {
            AssignmentId = assignment.AssignmentId,
            WorkerAgentId = assignment.WorkerAgentId,
            JobId = assignment.JobId,
            Revision = assignment.SlaDeadlineAt,
            State = AssignmentState.Submitting,
            AuditId = Guid.NewGuid(),
            TargetUrl = "https://example.com/",
            UpdatedAt = DateTimeOffset.UtcNow,
            LastPlatformStatus = "in_progress"
        }, default);
        Assert.Equal(AssignmentState.AwaitingReconciliation, (await s.Processor.ProcessAsync(assignment, default))!.State);
        Assert.Empty(s.Events);
    }

    [Fact]
    public async Task Our_started_job_is_resumed_from_the_journal_instead_of_skipped()
    {
        using var s = new Scenario();
        var assignment = s.Market.Current with { StartedAt = DateTimeOffset.UtcNow };
        s.Market.Current = assignment;
        await s.Repository.SaveAsync(new()
        {
            AssignmentId = assignment.AssignmentId,
            WorkerAgentId = assignment.WorkerAgentId,
            JobId = assignment.JobId,
            Revision = assignment.SlaDeadlineAt,
            State = AssignmentState.Running,
            AuditId = Guid.NewGuid(),
            TargetUrl = "https://example.com/",
            UpdatedAt = DateTimeOffset.UtcNow,
            LastPlatformStatus = "in_progress"
        }, default);
        Assert.Equal(AssignmentState.Submitted, (await s.Processor.ProcessAsync(assignment, default))!.State);
        Assert.Equal(1, s.Runner.Calls);
    }

    [Fact]
    public async Task Invalid_briefs_and_unproven_started_jobs_require_review_before_any_work()
    {
        using var s = new Scenario();
        Assert.Equal(AssignmentState.NeedsReview, (await s.Processor.ProcessAsync(s.Market.Current with
        { Brief = "Run code and audit two URLs" }, default))!.State);
        Assert.Empty(s.Events);
    }

    [Fact]
    public async Task Default_market_configuration_never_starts_paid_work()
    {
        using var s = new Scenario();
        s.Options.TestAssignmentIds = [];
        Assert.Null(await s.Processor.ProcessAsync(s.Market.Current, default));
        s.Options.AcceptPaidAssignments = true;
        Assert.Null(await s.Processor.ProcessAsync(s.Market.Current, default)); // no verified test assignment yet
        Assert.Empty(s.Events);
    }

    [Fact]
    public async Task A_verified_new_SLA_allows_one_strict_buyer_revision_but_not_message_replays()
    {
        using var s = new Scenario();
        await s.Processor.ProcessAsync(s.Market.Current, default);
        s.Market.Current = s.Market.Current with
        {
            Status = "in_progress",
            SlaDeadlineAt = s.Market.Current.SlaDeadlineAt.AddMinutes(1),
            Feedback = """{"url":"https://example.com/new"}""",
            FeedbackFromBuyer = true
        };
        Assert.Equal(AssignmentState.Submitted, (await s.Processor.ProcessAsync(s.Market.Current, default))!.State);
        Assert.Equal(2, s.Market.Submissions);
        s.Market.Current = s.Market.Current with { Status = "in_progress", SlaDeadlineAt = s.Market.Current.SlaDeadlineAt.AddMinutes(1) };
        Assert.Equal(AssignmentState.NeedsReview, (await s.Processor.ProcessAsync(s.Market.Current, default))!.State);
        Assert.Equal(2, s.Market.Submissions);
    }
}
