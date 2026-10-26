using NearWebsiteAuditAgent.Core;

namespace NearWebsiteAuditAgent.Infrastructure;

public static class JournalRetention
{
    public static Task CleanupAsync(IAssignmentRepository repository, AtomicFileStore store, int days,
        CancellationToken cancellationToken, TimeProvider? timeProvider = null)
        => CleanupCoreAsync(repository, store, days, cancellationToken, timeProvider ?? TimeProvider.System);
    private static async Task CleanupCoreAsync(IAssignmentRepository repository, AtomicFileStore store, int days,
        CancellationToken cancellationToken, TimeProvider clock)
    {
        var cutoff = clock.GetUtcNow().AddDays(-days);
        foreach (var work in await repository.ReadAllAsync(cancellationToken))
        {
            if (work.UpdatedAt >= cutoff || work.State is not (AssignmentState.Submitted or AssignmentState.Stopped) ||
                work.LastPlatformStatus is not ("submitted" or "accepted" or "cancelled" or "dispute_resolved")) continue;
            cancellationToken.ThrowIfCancellationRequested();
            if (work.JsonFile is not null) store.Remove(work.JsonFile);
            if (work.MarkdownFile is not null) store.Remove(work.MarkdownFile);
            store.Remove(work.Key + ".work.json");
        }
    }
}
