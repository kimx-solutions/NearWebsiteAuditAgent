using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NearWebsiteAuditAgent.Api;
using NearWebsiteAuditAgent.Contracts;
using NearWebsiteAuditAgent.Core;
using NearWebsiteAuditAgent.Infrastructure;

namespace NearWebsiteAuditAgent.Tests;

public class DirectApiTests
{
    internal const string Key = "DIRECT_API_TEST_KEY_0123456789_ABCDEF";
    internal static HttpRequestMessage Request(string json = "{\"url\":\"https://example.com/\"}", bool auth = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/audit") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (auth) request.Headers.Authorization = new("Bearer", Key);
        return request;
    }
    internal sealed class ApiFactory(IAuditObservationCollector? collector = null, IAuditRunner? runner = null,
        int requestsPerMinute = 60, string key = Key) : WebApplicationFactory<ApiProgram>
    {
        public readonly FixtureCollector Collector = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            { ["AuditApi:RequestsPerMinute"] = requestsPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<ApiKeyOptions>(options => options.Key = key);
                services.RemoveAll<IAuditObservationCollector>();
                services.AddSingleton(collector ?? Collector);
                if (runner is not null) { services.RemoveAll<IAuditRunner>(); services.AddSingleton(runner); }
            });
        }
        public HttpClient Client() => CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
    }
    internal sealed class FixtureCollector : IAuditObservationCollector
    {
        public int Calls;
        public Task<AuditObservation> CollectAsync(Uri target, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new AuditObservation
            {
                Main = new(new(target, 200, new Dictionary<string, string[]>(), "<title>fixture</title>", "text/html"), []),
                Html = new(1, 1, 0, 0, 0, 0, [], [], [])
            });
        }
    }
    [Fact]
    public async Task HTTP_API_reaches_controlled_TLS_target_through_real_collector_and_safe_transport()
    {
        var certificates = TlsTransportTests.Certificates("audit.example.com");
        using var root = certificates.Root;
        using var leaf = certificates.Leaf;
        await using var target = new HttpFixture(_ => new(200,
            "<title>API fixture</title><meta name=description content=fixture><h1>fixture</h1>",
            new Dictionary<string, string> { ["Content-Type"] = "text/html" }), leaf);
        var options = new AuditOptions();
        var transport = new SafeTargetTransport(options, new FixtureResolver(_ => [TransportTests.PublicIp]),
            new FixtureConnector(target.Port), [root]);
        var collector = new ObservationCollector(transport, new HtmlInspector(), options);
        await using var factory = new ApiFactory(collector);
        using var client = factory.Client();
        using var response = await client.SendAsync(Request("{\"url\":\"https://audit.example.com/private?token=URL_SECRET\"}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("URL_SECRET", body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(22, json.RootElement.GetProperty("checks").GetArrayLength());
        Assert.Contains(json.RootElement.GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("id").GetString() == "tls.certificate" && check.GetProperty("outcome").GetString() == "passed");
        Assert.Contains(target.Requests, request => request.Path.StartsWith("/private", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Authorized_HTTP_call_runs_real_orchestrator_and_returns_existing_report_schema()
    {
        await using var factory = new ApiFactory();
        using var client = factory.Client();
        using var response = await client.SendAsync(Request());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("1.0", json.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(22, json.RootElement.GetProperty("checks").GetArrayLength());
        Assert.Equal("https://example.com/", json.RootElement.GetProperty("url").GetString());
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("status").ValueKind);
        Assert.Equal(1, factory.Collector.Calls);
    }
    [Fact]
    public async Task Missing_or_wrong_credentials_do_not_read_or_audit_the_target()
    {
        await using var factory = new ApiFactory();
        using var client = factory.Client();
        foreach (var key in new[] { "", "INVALID_SECRET" })
        {
            using var request = Request(auth: false);
            if (key.Length > 0) request.Headers.Authorization = new("Bearer", key);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
            Assert.DoesNotContain("SECRET", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(0, factory.Collector.Calls);
    }
    [Theory]
    [InlineData("{\"url\":\"https://example.com\",\"headers\":{}}", 400)]
    [InlineData("{\"url\":\"https://example.com\",\"url\":\"https://other.example.com\"}", 400)]
    [InlineData("not JSON", 400)]
    [InlineData("{\"url\":\"http://127.0.0.1/SECRET\"}", 422)]
    [InlineData("{\"url\":\"https://user:SECRET@example.com/\"}", 422)]
    public async Task Invalid_or_restricted_inputs_fail_without_collection_or_secret_echo(string input, int status)
    {
        await using var factory = new ApiFactory();
        using var client = factory.Client();
        using var response = await client.SendAsync(Request(input));
        Assert.Equal(status, (int)response.StatusCode);
        Assert.DoesNotContain("SECRET", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, factory.Collector.Calls);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Body_size_limit_applies_to_known_and_unknown_length(bool knownLength)
    {
        await using var factory = new ApiFactory();
        using var client = factory.Client();
        using var request = Request();
        var bytes = Encoding.UTF8.GetBytes(new string('x', 8193));
        request.Content = knownLength ? new ByteArrayContent(bytes) : new StreamContent(new UnknownLengthStream(bytes));
        request.Content.Headers.ContentType = new("application/json");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, factory.Collector.Calls);
    }
    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/json; charset=utf-16")]
    public async Task Unsupported_media_types_are_rejected(string mediaType)
    {
        await using var factory = new ApiFactory();
        using var client = factory.Client();
        using var request = Request();
        request.Content!.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(0, factory.Collector.Calls);
    }
    [Fact]
    public async Task Rate_window_rejects_excess_work_before_running_it()
    {
        await using var factory = new ApiFactory(requestsPerMinute: 1);
        using var client = factory.Client();
        using var first = await client.SendAsync(Request());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var second = await client.SendAsync(Request());
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.NotNull(second.Headers.RetryAfter);
        Assert.Equal(1, factory.Collector.Calls);
    }
    [Fact]
    public async Task Unauthenticated_unknown_routes_cannot_consume_the_authenticated_rate_budget()
    {
        await using var factory = new ApiFactory(requestsPerMinute: 1);
        using var client = factory.Client();
        using var unknown = await client.GetAsync("/not-a-route");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var response = await client.SendAsync(Request());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Host_stopping_cancels_an_inflight_target_audit()
    {
        var collector = new BlockingCollector();
        await using var factory = new ApiFactory(collector);
        using var client = factory.Client();
        var pending = client.SendAsync(Request());
        await collector.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>().StopApplication();
        await collector.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException) { }
    }

    [Fact]
    public async Task Concurrency_has_no_waiting_queue_and_client_abort_releases_the_permit()
    {
        var collector = new BlockingCollector();
        await using var factory = new ApiFactory(collector);
        using var client = factory.Client();
        using var cancellation = new CancellationTokenSource();
        var first = client.SendAsync(Request(), cancellation.Token);
        await collector.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var second = await client.SendAsync(Request());
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await collector.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        collector.Block = false;
        using var third = await client.SendAsync(Request());
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
    }
    private sealed class BlockingCollector : IAuditObservationCollector
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Block = true;
        public async Task<AuditObservation> CollectAsync(Uri target, CancellationToken cancellationToken)
        {
            if (!Block) return await new FixtureCollector().CollectAsync(target, cancellationToken);
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            finally { Cancelled.TrySetResult(); }
            throw new InvalidOperationException();
        }
    }
}
