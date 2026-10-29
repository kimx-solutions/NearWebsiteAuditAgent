using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NearWebsiteAuditAgent.Contracts;
using NearWebsiteAuditAgent.Core;

namespace NearWebsiteAuditAgent.Tests;

public class ApiBoundaryTests
{
    [Fact]
    public async Task Unusable_Bearer_characters_fail_at_startup_instead_of_creating_an_inaccessible_service()
    {
        foreach (var key in new[] { "DIRECT_API_TEST_KEY_0123456789_ABC,DEF", new string('"', 33), new string('=', 32) })
        {
            await using var factory = new DirectApiTests.ApiFactory(key: key);
            Assert.ThrowsAny<Exception>(() => factory.Client());
        }
    }
    [Fact]
    public async Task Standard_Bearer_token_symbols_and_trailing_padding_can_authenticate()
    {
        const string key = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~+/==";
        await using var factory = new DirectApiTests.ApiFactory(key: key);
        using var client = factory.Client();
        using var request = DirectApiTests.Request();
        request.Headers.Authorization = new("Bearer", key);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("--AuditApi:ApiKey=CLI_SENTINEL_SECRET")]
    [InlineData("--AuditApi__ApiKey=CLI_SENTINEL_SECRET")]
    public async Task CLI_key_is_rejected_by_real_entry_point_without_starting_a_server(string argument)
        => Assert.Equal(1, await NearWebsiteAuditAgent.Api.ApiProgram.Main([argument]));

    [Fact]
    public async Task Real_Kestrel_socket_accepts_authenticated_loopback_requests_and_enforces_auth_and_size()
    {
        await using var factory = new DirectApiTests.ApiFactory();
        factory.UseKestrel(0);
        using var client = factory.CreateClient();
        Assert.Equal("http", client.BaseAddress?.Scheme);
        Assert.True(client.BaseAddress?.IsLoopback);
        using var unauthorized = await client.SendAsync(DirectApiTests.Request(auth: false));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var result = await client.SendAsync(DirectApiTests.Request());
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.True(result.Headers.CacheControl?.NoStore);
        using var tooLarge = await client.SendAsync(DirectApiTests.Request(new string('x', 8193)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal(1, factory.Collector.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("DIRECT_API_TEST_KEY_0123456789 ABCDEF")]
    public async Task Invalid_API_key_prevents_host_startup(string key)
    {
        await using var factory = new DirectApiTests.ApiFactory(key: key);
        Assert.ThrowsAny<Exception>(() => factory.Client());
    }
    [Fact]
    public async Task Invalid_rate_configuration_prevents_host_startup()
    {
        await using var factory = new DirectApiTests.ApiFactory(requestsPerMinute: 0);
        Assert.ThrowsAny<Exception>(() => factory.Client());
    }
    [Fact]
    public async Task Slow_body_times_out_without_entering_the_audit()
    {
        await using var factory = new DirectApiTests.ApiFactory();
        using var client = factory.Client();
        using var body = new SlowBody();
        var response = await factory.Server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Path = "/audit";
            context.Request.Scheme = "https";
            context.Request.ContentType = "application/json";
            context.Request.Headers.Authorization = "Bearer " + DirectApiTests.Key;
            context.Request.Body = body;
        }).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(408, response.Response.StatusCode);
        Assert.Equal(0, factory.Collector.Calls);
    }
    private sealed class SlowBody : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }

    [Fact]
    public async Task External_plain_HTTP_is_rejected_even_with_forged_forwarded_headers()
    {
        await using var factory = new DirectApiTests.ApiFactory();
        using var client = factory.Client();
        var response = await factory.Server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Path = "/audit";
            context.Request.Scheme = "http";
            context.Request.Headers.Authorization = "Bearer " + DirectApiTests.Key;
            context.Request.Headers["X-Forwarded-Proto"] = "https";
            context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
            context.Connection.RemoteIpAddress = IPAddress.Parse("8.8.8.8");
        });
        Assert.Equal(426, response.Response.StatusCode);
        Assert.Equal(0, factory.Collector.Calls);
    }
    [Fact]
    public async Task Duplicate_auth_or_query_cookie_credentials_cannot_authorize_work()
    {
        await using var factory = new DirectApiTests.ApiFactory();
        using var client = factory.Client();
        using var duplicate = DirectApiTests.Request(auth: false);
        duplicate.Headers.TryAddWithoutValidation("Authorization", new[] { "Bearer " + DirectApiTests.Key, "Bearer " + DirectApiTests.Key });
        using var duplicateResponse = await client.SendAsync(duplicate);
        Assert.Equal(HttpStatusCode.Unauthorized, duplicateResponse.StatusCode);
        using var query = DirectApiTests.Request(auth: false);
        query.RequestUri = new("/audit?apiKey=" + DirectApiTests.Key, UriKind.Relative);
        query.Headers.Add("Cookie", "apiKey=" + DirectApiTests.Key);
        using var queryResponse = await client.SendAsync(query);
        Assert.Equal(HttpStatusCode.Unauthorized, queryResponse.StatusCode);
        Assert.Equal(0, factory.Collector.Calls);
    }
    [Fact]
    public async Task Invalid_UTF8_and_encoded_requests_fail_before_audit()
    {
        await using var factory = new DirectApiTests.ApiFactory();
        using var client = factory.Client();
        using var utf8 = DirectApiTests.Request();
        utf8.Content = new ByteArrayContent([0xff, 0xfe]);
        utf8.Content.Headers.ContentType = new("application/json");
        using var utf8Response = await client.SendAsync(utf8);
        Assert.Equal(HttpStatusCode.BadRequest, utf8Response.StatusCode);
        using var compressed = DirectApiTests.Request();
        compressed.Content!.Headers.ContentEncoding.Add("gzip");
        using var compressedResponse = await client.SendAsync(compressed);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, compressedResponse.StatusCode);
        Assert.Equal(0, factory.Collector.Calls);
    }
    [Theory]
    [InlineData(AuditStatus.Failed, "network_failed", 502)]
    [InlineData(AuditStatus.Failed, "audit_timeout", 504)]
    [InlineData(AuditStatus.Partial, "audit_timeout", 200)]
    public async Task Target_failure_and_partial_status_remain_explicit(AuditStatus status, string code, int expected)
    {
        var result = new AuditResult { Status = status, Errors = [new(code, "safe error")] };
        await using var factory = new DirectApiTests.ApiFactory(runner: new ResultRunner(result));
        using var client = factory.Client();
        using var response = await client.SendAsync(DirectApiTests.Request());
        Assert.Equal(expected, (int)response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
    }
    [Fact]
    public async Task Nonterminal_runner_result_becomes_safe_problem_instead_of_a_misleading_audit()
    {
        await using var factory = new DirectApiTests.ApiFactory(runner: new ResultRunner(new() { Status = AuditStatus.Running }));
        using var client = factory.Client();
        using var response = await client.SendAsync(DirectApiTests.Request());
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("internal_error", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Oversized_report_is_not_sent_to_the_caller()
    {
        var result = new AuditResult { Status = AuditStatus.Succeeded, Url = new string('x', 196609) };
        await using var factory = new DirectApiTests.ApiFactory(runner: new ResultRunner(result));
        using var client = factory.Client();
        using var response = await client.SendAsync(DirectApiTests.Request());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("report_too_large", await response.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task Unexpected_exception_never_echoes_credentials_or_raw_exception_in_response_or_logs()
    {
        var logs = new CapturedLogs();
        await using var factory = new DirectApiTests.ApiFactory(runner: new ThrowingRunner());
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<ILoggerProvider>(logs)));
        using var client = configured.CreateClient(new() { BaseAddress = new("https://localhost") });
        using var response = await client.SendAsync(DirectApiTests.Request());
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("internal_error", json);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var output = json + string.Join("\n", logs.Messages);
        Assert.DoesNotContain("EXCEPTION_SECRET", output);
        Assert.DoesNotContain(DirectApiTests.Key, output);
    }
    private sealed class ResultRunner(AuditResult result) : IAuditRunner
    {
        public Task<AuditResult> RunAsync(AuditRequest request, CancellationToken cancellationToken, Guid? auditId = null)
            => Task.FromResult(result);
    }
    private sealed class ThrowingRunner : IAuditRunner
    {
        public Task<AuditResult> RunAsync(AuditRequest request, CancellationToken cancellationToken, Guid? auditId = null)
            => throw new IOException("EXCEPTION_SECRET");
    }
    private sealed class CapturedLogs : ILoggerProvider
    {
        public readonly System.Collections.Concurrent.ConcurrentBag<string> Messages = [];
        public ILogger CreateLogger(string categoryName) => new Capture(Messages);
        public void Dispose() { }
        private sealed class Capture(System.Collections.Concurrent.ConcurrentBag<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => messages.Add(formatter(state, exception) + exception?.Message);
        }
    }
}
