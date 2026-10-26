namespace NearWebsiteAuditAgent.Worker;

public interface IExecution
{
    Task<int> RunAsync(CancellationToken cancellationToken);
}
