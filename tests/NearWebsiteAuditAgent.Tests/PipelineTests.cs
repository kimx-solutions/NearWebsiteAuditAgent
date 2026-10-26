using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NearWebsiteAuditAgent.Contracts;
using NearWebsiteAuditAgent.Core;
using NearWebsiteAuditAgent.Infrastructure;

namespace NearWebsiteAuditAgent.Tests;

public class PipelineTests
{
    [Fact]
    public async Task The_full_local_pipeline_collects_only_the_submitted_page_and_writes_safe_reports()
    {
        var certificates = TlsTransportTests.Certificates("audit.example.com");
        using var root = certificates.Root;
        using var leaf = certificates.Leaf;
        await using var https = new HttpFixture(request => request.Path switch
        {
            "/" => new(200, """
                <title>Fixture</title><h1>one</h1><meta name="description" content="fixture">
                <link rel="canonical" href="/"><a href="/broken?value=URL_SECRET">broken</a>
                <a href="/fallback">fallback</a><a href="http://127.0.0.1/">private</a>
                <a href="https://other.example.com/">external</a><script src="/script">never execute</script>
                """, new Dictionary<string, string> { ["Content-Type"] = "text/html", ["Set-Cookie"] = "sid=COOKIE_SECRET; Secure; HttpOnly; SameSite=Lax" }),
            "/robots.txt" => new(200, "User-agent: *\nDisallow:", new Dictionary<string, string> { ["Content-Type"] = "text/plain" }),
            "/sitemap.xml" => new(200, "<urlset/>", new Dictionary<string, string> { ["Content-Type"] = "application/xml" }),
            "/fallback" when request.Method == "HEAD" => new(405),
            "/fallback" => new(200, "do not crawl this HTML"),
            _ => new(404)
        }, leaf);
        await using var http = new HttpFixture(_ => new(301, Headers: new Dictionary<string, string>
        { ["Location"] = "https://audit.example.com/" }));
        var options = new AuditOptions();
        var transport = new SafeTargetTransport(options, new FixtureResolver(_ => [TransportTests.PublicIp]),
            new PortConnector(http.Port, https.Port), [root]);
        var collector = new ObservationCollector(transport, new HtmlInspector(), options);
        var orchestrator = new AuditOrchestrator(collector, new CheckEngine(), options);
        var result = await orchestrator.RunAsync(new("https://audit.example.com/"), default);
        Assert.Equal(22, result.Checks.Count);
        Assert.Equal(1, result.Scope.HtmlPagesParsed);
        Assert.Equal(2, result.Scope.LinksSelected);
        Assert.Equal(2, result.Scope.LinksExcluded);
        Assert.Contains(result.Findings, f => f.Id == "broken_link");
        Assert.Equal(CheckOutcome.Passed, Assert.Single(result.Checks, c => c.Id == "tls.protocol").Outcome);
        Assert.Equal(CheckOutcome.Passed, Assert.Single(result.Checks, c => c.Id == "redirects.upgrade").Outcome);
        Assert.DoesNotContain(https.Requests, r => r.Path == "/script");
        Assert.Contains(https.Requests, r => r.Method == "GET" && r.Path == "/fallback");

        using var directory = new TemporaryDirectory();
        var store = new AtomicFileStore(directory.Path);
        using var processLock = store.AcquireLock();
        var reports = await new ReportWriter(store, options).WriteAsync(result, default);
        var json = await File.ReadAllTextAsync(System.IO.Path.Combine(store.Root, reports.JsonFile));
        var markdown = await File.ReadAllTextAsync(System.IO.Path.Combine(store.Root, reports.MarkdownFile));
        Assert.DoesNotContain("COOKIE_SECRET", json + markdown);
        Assert.DoesNotContain("URL_SECRET", json + markdown);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(result.AuditId.ToString(), parsed.RootElement.GetProperty("auditId").GetString());
        Assert.Contains(result.Score!.Value.ToString(), markdown);
        Assert.Contains("Coverage", markdown);
        Assert.All(parsed.RootElement.GetProperty("checks").EnumerateArray(),
            c => Assert.Equal(JsonValueKind.Array, c.GetProperty("evidence").ValueKind));
        if (Environment.GetEnvironmentVariable("NEAR_AUDIT_SAMPLE_DIR") is { Length: > 0 } export)
        {
            var exportRoot = Path.GetFullPath(export);
            if (!Directory.Exists(exportRoot)) throw new InvalidOperationException("Sample output directory must already exist.");
            await File.WriteAllTextAsync(Path.Combine(exportRoot, "website-audit.fixture.json"), json);
            await File.WriteAllTextAsync(Path.Combine(exportRoot, "website-audit.fixture.md"), markdown);
        }
    }

    [Fact]
    public async Task Rejected_inputs_produce_no_target_connections_and_no_fake_100_score()
    {
        var collector = new CountingCollector();
        var result = await new AuditOrchestrator(collector, new(), new()).RunAsync(new("https://user:INPUT_SECRET@127.0.0.1/"), default);
        Assert.Equal(AuditStatus.Rejected, result.Status);
        Assert.Equal(0, collector.Calls);
        Assert.Null(result.Score);
        Assert.Null(result.Grade);
        Assert.All(result.Checks, c => Assert.Equal(CheckOutcome.NotTested, c.Outcome));
        Assert.DoesNotContain("INPUT_SECRET", JsonSerializer.Serialize(result, ReportJson.Options));
    }

    [Fact]
    public async Task Cancellation_is_distinct_from_target_failure_and_the_next_job_can_run()
    {
        var collector = new CountingCollector();
        var runner = new AuditOrchestrator(collector, new(), new());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(AuditStatus.Cancelled, (await runner.RunAsync(new("https://example.com/"), cancellation.Token)).Status);
        var next = await runner.RunAsync(new("https://example.com/"), default);
        Assert.Equal(AuditStatus.Failed, next.Status);
        Assert.Equal(1, collector.Calls);
    }

    [Fact]
    public void Markdown_escapes_untrusted_text_instead_of_emitting_HTML_or_images()
    {
        var result = new AuditResult
        {
            Findings = [new("x", "seo.title", "seo", Severity.Info, "<script>bad()</script> ![image](https://evil.example/)", [], "[click](javascript:bad)")]
        };
        var markdown = ReportWriter.Markdown(result);
        Assert.DoesNotContain("<script>", markdown);
        Assert.DoesNotContain("![image](", markdown);
        Assert.DoesNotContain("[click](", markdown);
    }

    private sealed class CountingCollector : IAuditObservationCollector
    {
        public int Calls { get; private set; }
        public Task<AuditObservation> CollectAsync(Uri target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new AuditObservation { Main = new(null, [], ErrorCode: "network_failed") });
        }
    }
    private sealed class PortConnector(int http, int https) : IAddressConnector
    {
        public async Task<ConnectedStream> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, port == 443 ? https : http, cancellationToken);
                return new(new NetworkStream(socket, ownsSocket: true), address);
            }
            catch { socket.Dispose(); throw; }
        }
    }
}
internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "near-audit-tests-" + Guid.NewGuid().ToString("N"));
    public TemporaryDirectory() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        var full = System.IO.Path.GetFullPath(Path);
        var prefix = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar)
            + System.IO.Path.DirectorySeparatorChar + "near-audit-tests-";
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(full)) return;
        Directory.Delete(full, recursive: true);
    }
}
