using NearWebsiteAuditAgent.Contracts;

namespace NearWebsiteAuditAgent.Core;

public interface ISafeTargetTransport
{
    Task<FetchObservation> FetchAsync(Uri target, HttpMethod method, AuditBudget budget,
        Uri? scopeOrigin, CancellationToken cancellationToken);
}
public sealed class AuditBudget(AuditOptions options) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(options.MaxConcurrentRequestsPerAudit);
    private readonly object _gate = new();
    private readonly HashSet<string> _limits = new(StringComparer.Ordinal);
    private int _attempts;
    private long _bytes;
    public AuditOptions Options { get; } = options;
    public void TakeAttempt()
    {
        lock (_gate)
        {
            if (_attempts >= Options.MaxTargetAttempts)
            {
                _limits.Add("max_target_attempts");
                throw new AuditException("budget_exceeded");
            }
            _attempts++;
        }
    }
    public void AddBytes(int count)
    {
        lock (_gate)
        {
            if (count < 0 || _bytes + count > Options.MaxTotalResponseBytes)
            {
                _limits.Add("max_total_response_bytes");
                throw new AuditException("budget_exceeded");
            }
            _bytes += count;
        }
    }
    public void Limit(string reason) { lock (_gate) _limits.Add(reason); }
    public AuditScope Snapshot()
    {
        lock (_gate) return new() { TargetAttempts = _attempts, DecodedBodyBytes = _bytes, LimitsReached = _limits.Order(StringComparer.Ordinal).ToArray() };
    }
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken);
        return new Lease(_slots);
    }
    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
    public void Dispose() => _slots.Dispose();
}
