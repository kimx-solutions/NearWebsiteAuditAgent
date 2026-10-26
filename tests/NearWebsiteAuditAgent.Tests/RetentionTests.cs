using NearWebsiteAuditAgent.Contracts;
using NearWebsiteAuditAgent.Core;
using NearWebsiteAuditAgent.Infrastructure;

namespace NearWebsiteAuditAgent.Tests;

public class RetentionTests
{
    [Fact]
    public async Task Cleanup_removes_only_old_reconciled_work_and_keeps_unknown_submissions_and_validation_metadata()
    {
        using var directory = new TemporaryDirectory();
        var store = new AtomicFileStore(directory.Path);
        var repository = new FileAssignmentRepository(store);
        async Task<AssignmentWork> Save(AssignmentState state)
        {
            var auditId = Guid.NewGuid();
            var files = await new ReportWriter(store, new()).WriteAsync(new AuditResult { AuditId = auditId }, default);
            var work = new AssignmentWork
            {
                AssignmentId = Guid.NewGuid(),
                JobId = Guid.NewGuid(),
                WorkerAgentId = MarketClientTests.AgentId,
                Revision = DateTimeOffset.UtcNow.AddDays(-10),
                UpdatedAt = DateTimeOffset.UtcNow.AddDays(-8),
                AuditId = auditId,
                State = state,
                LastPlatformStatus = state == AssignmentState.Submitted ? "submitted" : "in_progress",
                TargetUrl = "https://example.com/",
                JsonFile = files.JsonFile,
                MarkdownFile = files.MarkdownFile,
                MarkdownHash = files.MarkdownHash
            };
            await repository.SaveAsync(work, default);
            return work;
        }
        var complete = await Save(AssignmentState.Submitted);
        var unknown = await Save(AssignmentState.AwaitingReconciliation);
        await repository.SaveValidationAsync(new(MarketClientTests.AgentId, complete.AssignmentId, DateTimeOffset.UtcNow), default);
        await JournalRetention.CleanupAsync(repository, store, 7, default);
        Assert.False(File.Exists(Path.Combine(store.Root, complete.MarkdownFile!)));
        Assert.False(File.Exists(Path.Combine(store.Root, complete.Key + ".work.json")));
        Assert.True(File.Exists(Path.Combine(store.Root, unknown.MarkdownFile!)));
        Assert.Equal(unknown.AssignmentId, Assert.Single(await repository.ReadAllAsync(default)).AssignmentId);
        Assert.NotNull(await repository.ReadValidationAsync(default));
    }
}
