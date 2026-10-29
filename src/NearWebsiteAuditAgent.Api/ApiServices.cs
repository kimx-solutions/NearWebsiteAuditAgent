using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using NearWebsiteAuditAgent.Core;
using NearWebsiteAuditAgent.Infrastructure;

namespace NearWebsiteAuditAgent.Api;

public sealed class ApiKeyOptions
{
    public string Key { get; set; } = "";
}
public sealed class AuditApiOptions
{
    public int RequestsPerMinute { get; set; } = 60;
}
internal static class ApiServices
{
    public static IServiceCollection AddDirectAuditApi(this IServiceCollection services)
    {
        services.AddOptions<ApiKeyOptions>().Configure(options =>
            options.Key = Environment.GetEnvironmentVariable("AuditApi__ApiKey") ?? "")
            .Validate(options => ValidApiKey(options.Key),
                "AuditApi:ApiKey must be supplied securely.").ValidateOnStart();
        services.AddOptions<AuditApiOptions>().BindConfiguration("AuditApi")
            .Validate(options => options.RequestsPerMinute is >= 1 and <= 600, "AuditApi:RequestsPerMinute is invalid.").ValidateOnStart();
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IConfiguration>().GetSection("Audit").Get<AuditOptions>() ?? new();
            ConfigurationValidation.ValidateAudit(options);
            return options;
        });
        services.AddHostedService<StartupValidation>();
        services.AddSingleton<IHostResolver, SystemHostResolver>();
        services.AddSingleton<IAddressConnector, SocketAddressConnector>();
        services.AddSingleton<ISafeTargetTransport>(provider => new SafeTargetTransport(provider.GetRequiredService<AuditOptions>(),
            provider.GetRequiredService<IHostResolver>(), provider.GetRequiredService<IAddressConnector>()));
        services.AddSingleton<IHtmlInspector, HtmlInspector>();
        services.AddSingleton<IAuditObservationCollector, ObservationCollector>();
        services.AddSingleton<CheckEngine>();
        services.AddSingleton<IAuditRunner, AuditOrchestrator>();
        services.AddAuthentication("ApiKey").AddScheme<AuthenticationSchemeOptions, ApiKeyHandler>("ApiKey", _ => { });
        services.AddAuthorization();
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<AuditOptions, IOptions<AuditApiOptions>>((options, audit, api) =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained<HttpContext>(
                PartitionedRateLimiter.Create<HttpContext, string>(context => context.User.Identity?.IsAuthenticated == true
                    ? RateLimitPartition.GetFixedWindowLimiter("api", _ => new()
                    { PermitLimit = api.Value.RequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
                    : RateLimitPartition.GetNoLimiter("anonymous")),
                PartitionedRateLimiter.Create<HttpContext, string>(context => context.User.Identity?.IsAuthenticated == true
                    ? RateLimitPartition.GetConcurrencyLimiter("api", _ => new()
                    { PermitLimit = audit.MaxConcurrentAudits, QueueLimit = 0 })
                    : RateLimitPartition.GetNoLimiter("anonymous")));
            options.OnRejected = async (context, _) =>
            {
                var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)) : 1;
                context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await ApiErrors.WriteAsync(context.HttpContext, 429, "rate_limited");
            };
        });
        return services;
    }
    private static bool ValidApiKey(string key)
    {
        var token = key.TrimEnd('=');
        return key.Length is >= 32 and <= 256 && token.Length > 0 &&
            token.All(character => char.IsAsciiLetterOrDigit(character) || "-._~+/".Contains(character));
    }
    private sealed class StartupValidation(AuditOptions options) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) { ConfigurationValidation.ValidateAudit(options); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
