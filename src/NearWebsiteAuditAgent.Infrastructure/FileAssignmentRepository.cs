using System.Text.Json;
using NearWebsiteAuditAgent.Contracts;
using NearWebsiteAuditAgent.Core;

namespace NearWebsiteAuditAgent.Infrastructure;

public sealed class FileAssignmentRepository(AtomicFileStore store) : IAssignmentRepository
{
    public async Task<IReadOnlyList<AssignmentWork>> ReadAllAsync(CancellationToken cancellationToken)
    {
        var records = new List<AssignmentWork>();
        foreach (var name in store.Names(".work.json"))
        {
            try
            {
                var bytes = await store.ReadAsync(name, cancellationToken);
                using var document = JsonDocument.Parse(bytes);
                var fields = document.RootElement.EnumerateObject().ToArray();
                if (fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length ||
                    new[] { "schemaVersion", "assignmentId", "workerAgentId", "jobId", "revision", "state", "auditId", "updatedAt",
                        "lastPlatformStatus", "startAttempts", "auditAttempts" }.Any(key => !document.RootElement.TryGetProperty(key, out _)))
                    throw new AuditException("storage_unavailable");
                var record = JsonSerializer.Deserialize<AssignmentWork>(bytes, ReportJson.Options)
                    ?? throw new AuditException("storage_unavailable");
                Validate(record);
                if (name != record.Key + ".work.json") throw new AuditException("storage_unavailable");
                records.Add(record);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or IOException)
            { throw new AuditException("storage_unavailable"); }
        }
        return records;
    }
    public Task SaveAsync(AssignmentWork work, CancellationToken cancellationToken)
    {
        Validate(work);
        return store.WriteAsync(work.Key + ".work.json", JsonSerializer.SerializeToUtf8Bytes(work, ReportJson.Options),
            true, cancellationToken);
    }
    public async Task<MarketValidation?> ReadValidationAsync(CancellationToken cancellationToken)
    {
        if (!store.Names("market-validation.json").Contains("market-validation.json")) return null;
        try
        {
            var validation = JsonSerializer.Deserialize<MarketValidation>(
                await store.ReadAsync("market-validation.json", cancellationToken), ReportJson.Options);
            if (validation is null || validation.WorkerAgentId == Guid.Empty || validation.TestAssignmentId == Guid.Empty ||
                validation.VerifiedAt == default) throw new AuditException("storage_unavailable");
            return validation;
        }
        catch (JsonException) { throw new AuditException("storage_unavailable"); }
    }
    public Task SaveValidationAsync(MarketValidation validation, CancellationToken cancellationToken)
        => store.WriteAsync("market-validation.json", JsonSerializer.SerializeToUtf8Bytes(validation, ReportJson.Options), true, cancellationToken);
    private static void Validate(AssignmentWork work)
    {
        if (work.SchemaVersion != 1 || work.AssignmentId == Guid.Empty || work.JobId == Guid.Empty || work.WorkerAgentId == Guid.Empty ||
            work.Revision == default || work.UpdatedAt == default || work.AuditId == Guid.Empty || !Enum.IsDefined(work.State) ||
            work.StartAttempts < 0 || work.AuditAttempts < 0) throw new AuditException("storage_unavailable");
        var stem = work.AuditId.ToString("N");
        if ((work.JsonFile is not null && work.JsonFile != stem + ".json") ||
            (work.MarkdownFile is not null && work.MarkdownFile != stem + ".md")) throw new AuditException("storage_unavailable");
        if (work.State is AssignmentState.ReportReady or AssignmentState.Submitted &&
            (work.MarkdownFile is null || work.JsonFile is null || work.MarkdownHash?.Length != 64))
            throw new AuditException("storage_unavailable");
    }
}
