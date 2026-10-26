using System.Text;
using NearWebsiteAuditAgent.Contracts;
using NearWebsiteAuditAgent.Core;
using NearWebsiteAuditAgent.Infrastructure;

namespace NearWebsiteAuditAgent.Worker;

public sealed class LocalExecution(IAuditRunner runner, IReportWriter reports, AtomicFileStore store,
    ExecutionOptions options, ILogger<LocalExecution> logger) : IExecution
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var auditStarted = false;
        try
        {
            using var instance = store.AcquireLock();
            var file = new FileInfo(options.InputFile);
            if (!file.Exists || file.Length > 8192) throw new AuditException("invalid_input");
            var json = await File.ReadAllTextAsync(file.FullName, new UTF8Encoding(false, true), cancellationToken);
            var input = AuditInput.Parse(json);
            auditStarted = true;
            var result = await runner.RunAsync(input, cancellationToken);
            // A cancelled caller does not start new storage or publication work.
            if (cancellationToken.IsCancellationRequested) return 5;
            var files = await reports.WriteAsync(result, cancellationToken);
            logger.LogInformation("audit_completed {AuditId} {Status} {JsonFile} {MarkdownFile}",
                result.AuditId, result.Status, files.JsonFile, files.MarkdownFile);
            return result.Status switch
            {
                AuditStatus.Succeeded => 0,
                AuditStatus.Partial => 2,
                AuditStatus.Rejected => 3,
                AuditStatus.Cancelled => 5,
                _ => 4
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return 5; }
        catch (AuditException error)
        {
            logger.LogError("audit_error {Code}", error.Code);
            return error.Code == "invalid_input" ? 3 : auditStarted ? 4 : 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            logger.LogError("audit_error {Code}", auditStarted ? "storage_unavailable" : "invalid_input");
            return auditStarted ? 4 : 3;
        }
    }
}
