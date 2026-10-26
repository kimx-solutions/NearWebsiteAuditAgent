using NearWebsiteAuditAgent.Core;
using NearWebsiteAuditAgent.Infrastructure;

namespace NearWebsiteAuditAgent.Tests;

public class OpenSslAiaTests
{
    [Fact]
    public async Task A_TLS_client_cannot_follow_certificate_AIA_to_internal_HTTP()
    {
        // OpenSSL sends the leaf without Schannel's independent server-side chain retrieval.
        await using var aia = new HttpFixture(_ => new(404));
        var certificates = TlsTransportTests.Certificates("audit.example.com", aia: "http://127.0.0.1:" + aia.Port + "/issuer.cer");
        using var root = certificates.Root;
        using var leaf = certificates.Leaf;
        using var directory = new TemporaryDirectory();
        await using var server = new OpenSslFixture(directory.Path, leaf);
        await server.ReadyAsync();
        var options = new AuditOptions();
        using var budget = new AuditBudget(options);
        var transport = new SafeTargetTransport(options, new FixtureResolver(_ => [TransportTests.PublicIp]),
            new FixtureConnector(server.Port), [root]);
        var result = await transport.FetchAsync(new("https://audit.example.com/"), HttpMethod.Get, budget, null, default);
        Assert.Equal("tls_validation_failed", result.ErrorCode);
        Assert.NotNull(result.Tls);
        Assert.False(result.Tls.ChainTrusted);
        Assert.Empty(aia.Requests);
    }
}
