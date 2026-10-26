using Microsoft.Extensions.Logging.Abstractions;
using NearWebsiteAuditAgent.Core;
using NearWebsiteAuditAgent.Worker;

namespace NearWebsiteAuditAgent.Tests;

public class MarketExecutionTests
{
    [Fact]
    public async Task Poll_deadline_does_not_cancel_an_assignment_with_a_longer_audit_budget()
    {
        using var s = new MarketWorkTests.Scenario();
        using var host = new CancellationTokenSource();
        var clock = new PollClock();
        s.Runner.OnRun = token =>
        {
            clock.ExpirePoll();
            Assert.False(token.IsCancellationRequested);
        };
        s.Market.AfterSubmit = host.Cancel;
        var execution = new MarketExecution(s.Market, s.Repository, s.Processor, s.Store, s.Options,
            new StorageOptions { Root = s.Store.Root }, NullLogger<MarketExecution>.Instance, clock);
        Assert.Equal(0, await execution.RunAsync(host.Token));
        Assert.Equal(1, s.Market.Submissions);
    }
    private sealed class PollClock : TimeProvider
    {
        private readonly List<PollTimer> _timers = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new PollTimer(callback, state);
            _timers.Add(timer);
            return timer;
        }
        public void ExpirePoll() { foreach (var timer in _timers.ToArray()) timer.Fire(); }
        private sealed class PollTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            public void Fire() { if (!_disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    [Fact]
    public async Task A_worker_cycle_processes_allowed_work_and_reconciles_it_without_duplicate_execution()
    {
        using var s = new MarketWorkTests.Scenario();
        var execution = new MarketExecution(s.Market, s.Repository, s.Processor, s.Store, s.Options,
            new StorageOptions { Root = s.Store.Root }, NullLogger<MarketExecution>.Instance);
        await execution.RunCycleAsync(default);
        await execution.RunCycleAsync(default);
        Assert.Equal(1, s.Market.Submissions);
        Assert.Equal(1, s.Runner.Calls);
        Assert.NotNull(await s.Repository.ReadValidationAsync(default));
    }
}
