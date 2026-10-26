using System.Net;
using System.Text.Json;
using NearWebsiteAuditAgent.Contracts;
using NearWebsiteAuditAgent.Core;
using NearWebsiteAuditAgent.Infrastructure;
using NearWebsiteAuditAgent.Worker;

try
{
    ConfigurationValidation.ValidateArguments(args);
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory
    });
    var execution = builder.Configuration.GetSection("Execution").Get<ExecutionOptions>() ?? new();
    var audit = builder.Configuration.GetSection("Audit").Get<AuditOptions>() ?? new();
    var storage = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new();
    var market = builder.Configuration.GetSection("Market").Get<MarketOptions>() ?? new();
    // Never accept a token from a command-line argument or a checked-in settings file.
    market.AgentApiToken = Environment.GetEnvironmentVariable("Market__AgentApiToken") ??
        Environment.GetEnvironmentVariable("AGENT_API_TOKEN") ?? "";
    if (builder.Configuration["Market:BaseUrl"] is { Length: > 0 } baseUrl &&
        baseUrl.TrimEnd('/') != "https://market.near.ai") throw new AuditException("configuration_invalid");
    ConfigurationValidation.Validate(execution, audit, storage, market);

    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(format => { format.UseUtcTimestamp = true; format.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ"; });
    builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.None);
    builder.Services.AddWindowsService(service => service.ServiceName = "NearWebsiteAuditAgent");
    builder.Services.AddSingleton(execution);
    builder.Services.AddSingleton(audit);
    builder.Services.AddSingleton(storage);
    builder.Services.AddSingleton(market);
    builder.Services.AddSingleton(_ => new AtomicFileStore(storage.Root, storage.MaxBytes));
    builder.Services.AddSingleton<IReportContentReader>(services => services.GetRequiredService<AtomicFileStore>());
    builder.Services.AddSingleton<IHostResolver, SystemHostResolver>();
    builder.Services.AddSingleton<IAddressConnector, SocketAddressConnector>();
    builder.Services.AddSingleton<ISafeTargetTransport>(services => new SafeTargetTransport(audit,
        services.GetRequiredService<IHostResolver>(), services.GetRequiredService<IAddressConnector>()));
    builder.Services.AddSingleton<IHtmlInspector, HtmlInspector>();
    builder.Services.AddSingleton<IAuditObservationCollector, ObservationCollector>();
    builder.Services.AddSingleton<CheckEngine>();
    builder.Services.AddSingleton<IAuditRunner, AuditOrchestrator>();
    builder.Services.AddSingleton<IReportWriter, ReportWriter>();
    builder.Services.AddSingleton<IAssignmentRepository, FileAssignmentRepository>();
    if (execution.Mode == "Local") builder.Services.AddSingleton<IExecution, LocalExecution>();
    else
    {
        builder.Services.AddHttpClient("market", client =>
        {
            client.BaseAddress = new("https://market.near.ai/");
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestVersion = HttpVersion.Version11;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }).RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            Credentials = null,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            MaxResponseHeadersLength = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            SslOptions = new() { CertificateChainPolicy = CertificatePolicies.Offline() }
        });
        builder.Services.AddSingleton<IMarketClient>(services =>
            new MarketClient(services.GetRequiredService<IHttpClientFactory>().CreateClient("market"), market));
        builder.Services.AddSingleton<MarketWorkProcessor>();
        builder.Services.AddSingleton<IExecution, MarketExecution>();
    }
    builder.Services.AddHostedService<Worker>();
    using var host = builder.Build();
    await host.RunAsync();
    return Environment.ExitCode;
}
catch (AuditException error)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { error = error.Code, settingKey = error.SettingKey }, ReportJson.Options));
    return 1;
}
catch (Exception error) when (error is not OutOfMemoryException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { error = "configuration_invalid", type = error.GetType().Name }, ReportJson.Options));
    return 1;
}
