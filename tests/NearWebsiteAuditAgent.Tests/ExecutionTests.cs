using Microsoft.Extensions.Logging.Abstractions;
using NearWebsiteAuditAgent.Core;
using NearWebsiteAuditAgent.Infrastructure;
using NearWebsiteAuditAgent.Worker;

namespace NearWebsiteAuditAgent.Tests;

public class ExecutionTests
{
    [Fact]
    public async Task Local_execution_writes_a_rejected_result_without_disclosing_input_secrets()
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "input.json");
        await File.WriteAllTextAsync(input, """{"url":"https://user:EXECUTION_SECRET@127.0.0.1/"}""");
        var store = new AtomicFileStore(Path.Combine(directory.Path, "reports"));
        var options = new AuditOptions();
        var runner = new AuditOrchestrator(new ForbiddenCollector(), new(), options);
        var execution = new LocalExecution(runner, new ReportWriter(store, options), store,
            new() { Mode = "Local", InputFile = input }, NullLogger<LocalExecution>.Instance);
        Assert.Equal(3, await execution.RunAsync(default));
        var report = Assert.Single(Directory.EnumerateFiles(store.Root, "*.json"));
        Assert.DoesNotContain("EXECUTION_SECRET", await File.ReadAllTextAsync(report));
    }

    [Fact]
    public async Task Malformed_input_exits_with_no_fabricated_audit_report()
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "input.json");
        await File.WriteAllTextAsync(input, "audit whatever");
        var store = new AtomicFileStore(Path.Combine(directory.Path, "reports"));
        var options = new AuditOptions();
        var execution = new LocalExecution(new AuditOrchestrator(new ForbiddenCollector(), new(), options),
            new ReportWriter(store, options), store, new() { Mode = "Local", InputFile = input }, NullLogger<LocalExecution>.Instance);
        Assert.Equal(3, await execution.RunAsync(default));
        Assert.Empty(Directory.EnumerateFiles(store.Root, "*.json"));
    }

    private sealed class ForbiddenCollector : IAuditObservationCollector
    {
        public Task<AuditObservation> CollectAsync(Uri target, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Restricted inputs must never reach observation collection.");
    }
}
