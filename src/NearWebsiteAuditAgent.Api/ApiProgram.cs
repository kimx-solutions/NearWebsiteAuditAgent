using System.Text.Json;
using Microsoft.Extensions.Options;
using NearWebsiteAuditAgent.Core;

namespace NearWebsiteAuditAgent.Api;

public sealed class ApiProgram
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Any(argument => argument.Contains("ApiKey", StringComparison.OrdinalIgnoreCase)))
                throw new AuditException("token_on_command_line", "AuditApi:ApiKey");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
            if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://127.0.0.1:5080");
            builder.WebHost.ConfigureKestrel(server =>
            {
                server.AddServerHeader = false;
                server.Limits.MaxRequestBodySize = 8192;
                server.Limits.MaxRequestHeadersTotalSize = 32768;
                server.Limits.MaxConcurrentConnections = 100;
                server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            });
            builder.Logging.ClearProviders();
            builder.Logging.AddJsonConsole();
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.None);
            builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.None);
            builder.Services.AddDirectAuditApi();
            await using var app = builder.Build();
            app.UseMiddleware<ApiBoundary>();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseRateLimiter();
            app.MapPost("/audit", AuditEndpoint.HandleAsync).RequireAuthorization()
                .WithName("AuditWebsite").Produces<Contracts.AuditResult>()
                .Produces<Contracts.AuditResult>(422).Produces<Contracts.AuditResult>(502).Produces<Contracts.AuditResult>(504);
            await app.RunAsync();
            return 0;
        }
        catch (Exception error) when (error is AuditException or OptionsValidationException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { error = "configuration_invalid", settingKey = "AuditApi/Audit" }));
            return 1;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { error = "startup_failed" }));
            return 1;
        }
    }
}
