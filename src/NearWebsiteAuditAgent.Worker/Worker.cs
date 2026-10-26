namespace NearWebsiteAuditAgent.Worker;

public sealed class Worker(IExecution execution, IHostApplicationLifetime lifetime, ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { Environment.ExitCode = await execution.RunAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { Environment.ExitCode = 0; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Never hand a remote exception or request to a logging provider.
            logger.LogError("execution_failed {Code} {Type}", "internal_error", error.GetType().Name);
            Environment.ExitCode = 1;
        }
        finally { lifetime.StopApplication(); }
    }
}
