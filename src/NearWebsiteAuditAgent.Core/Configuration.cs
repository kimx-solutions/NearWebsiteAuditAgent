namespace NearWebsiteAuditAgent.Core;

public sealed class ExecutionOptions
{
    public string Mode { get; set; } = "";
    public string InputFile { get; set; } = "";
}
public sealed class StorageOptions
{
    public string Root { get; set; } = "";
    public long MaxBytes { get; set; } = 104857600;
    public int RetentionDays { get; set; } = 7;
}
public static class ConfigurationValidation
{
    public static void Validate(ExecutionOptions execution, AuditOptions audit, StorageOptions storage, MarketOptions market)
    {
        Require(execution.Mode is "Local" or "Market", "Execution:Mode");
        Require(execution.Mode != "Local" || !string.IsNullOrWhiteSpace(execution.InputFile), "Execution:InputFile");
        Require(!string.IsNullOrWhiteSpace(storage.Root), "Storage:Root");
        Require(storage.MaxBytes is > 0 and <= 1073741824, "Storage:MaxBytes");
        Require(storage.RetentionDays is >= 1 and <= 90, "Storage:RetentionDays");
        Require(audit.MaxDurationSeconds is >= 1 and <= 300, "Audit:MaxDurationSeconds");
        Require(audit.RequestTimeoutSeconds >= 1 && audit.RequestTimeoutSeconds <= audit.MaxDurationSeconds, "Audit:RequestTimeoutSeconds");
        Require(audit.DnsTimeoutSeconds >= 1 && audit.DnsTimeoutSeconds <= audit.RequestTimeoutSeconds, "Audit:DnsTimeoutSeconds");
        Require(audit.ConnectTimeoutSeconds >= 1 && audit.ConnectTimeoutSeconds <= audit.RequestTimeoutSeconds, "Audit:ConnectTimeoutSeconds");
        Require(audit.MaxRedirects is >= 1 and <= 10, "Audit:MaxRedirects");
        Require(audit.MaxTargetAttempts is >= 1 and <= 200, "Audit:MaxTargetAttempts");
        Require(audit.MaxLinks is >= 1 and <= 50, "Audit:MaxLinks");
        Require(audit.MaxHtmlPages == 1, "Audit:MaxHtmlPages");
        Require(audit.MaxResponseBytes is >= 1 and <= 8388608, "Audit:MaxResponseBytes");
        Require(audit.MaxTotalResponseBytes >= audit.MaxResponseBytes && audit.MaxTotalResponseBytes <= 33554432, "Audit:MaxTotalResponseBytes");
        Require(audit.MaxResponseHeaderBytes is >= 1 and <= 131072, "Audit:MaxResponseHeaderBytes");
        Require(audit.MaxReportBytes is >= 1024 and <= 196608, "Audit:MaxReportBytes");
        Require(audit.MaxConcurrentAudits is >= 1 and <= 8, "Audit:MaxConcurrentAudits");
        Require(audit.MaxConcurrentRequestsPerAudit is >= 1 and <= 8, "Audit:MaxConcurrentRequestsPerAudit");
        Require(storage.MaxBytes >= 2L * audit.MaxReportBytes + 65536, "Storage:MaxBytes");
        if (execution.Mode != "Market") return;
        Require(market.AgentApiToken.StartsWith("aat_", StringComparison.Ordinal) && market.AgentApiToken.Length is > 4 and <= 4096 &&
            !market.AgentApiToken.Any(c => c <= 32 || c >= 127), "Market:AgentApiToken");
        Require(market.RequestTimeoutSeconds is >= 1 and <= 60, "Market:RequestTimeoutSeconds");
        Require(market.PollIntervalSeconds is >= 1 and <= 3600, "Market:PollIntervalSeconds");
        Require(market.MaxReadAttempts is >= 1 and <= 5, "Market:MaxReadAttempts");
        Require(market.MaxStartAttempts is >= 1 and <= 5, "Market:MaxStartAttempts");
        Require(market.SubmissionReserveSeconds >= market.RequestTimeoutSeconds && market.SubmissionReserveSeconds <= 300, "Market:SubmissionReserveSeconds");
        Require(market.MaxPages is >= 1 and <= 100, "Market:MaxPages");
        Require(market.TestAssignmentIds.Length <= 50 && !market.TestAssignmentIds.Contains(Guid.Empty), "Market:TestAssignmentIds");
    }
    public static void ValidateArguments(string[] arguments)
    {
        if (arguments.Any(argument => argument.Contains("AgentApiToken", StringComparison.OrdinalIgnoreCase) ||
            argument.Contains("AGENT_API_TOKEN", StringComparison.OrdinalIgnoreCase)))
            throw new AuditException("token_on_command_line", "Market:AgentApiToken");
    }
    private static void Require(bool valid, string key)
    {
        if (!valid) throw new AuditException("configuration_invalid", key);
    }
}
