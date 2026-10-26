using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NearWebsiteAuditAgent.Core;
using NearWebsiteAuditAgent.Infrastructure;

namespace NearWebsiteAuditAgent.Tests;

public class TlsTransportTests
{
    internal static (X509Certificate2 Root, X509Certificate2 Leaf) Certificates(string hostname, bool expired = false, string? aia = null)
    {
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Audit Fixture Root " + Guid.NewGuid(), rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest("CN=" + hostname, leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(hostname);
        leafRequest.CertificateExtensions.Add(names.Build());
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        X509Certificate2? intermediate = null;
        if (aia is not null)
        {
            using var intermediateKey = RSA.Create(2048);
            var intermediateRequest = new CertificateRequest("CN=Missing Fixture Intermediate", intermediateKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            using var intermediatePublic = intermediateRequest.Create(root, DateTimeOffset.UtcNow.AddMonths(-6), DateTimeOffset.UtcNow.AddMonths(6), RandomNumberGenerator.GetBytes(16));
            intermediate = intermediatePublic.CopyWithPrivateKey(intermediateKey);
            var writer = new AsnWriter(AsnEncodingRules.DER);
            writer.PushSequence();
            writer.PushSequence();
            writer.WriteObjectIdentifier("1.3.6.1.5.5.7.48.2");
            writer.WriteCharacterString(UniversalTagNumber.IA5String, aia, new Asn1Tag(TagClass.ContextSpecific, 6));
            writer.PopSequence();
            writer.PopSequence();
            leafRequest.CertificateExtensions.Add(new X509Extension("1.3.6.1.5.5.7.1.1", writer.Encode(), false));
        }
        try
        {
            using var publicLeaf = leafRequest.Create(intermediate ?? root, DateTimeOffset.UtcNow.AddDays(-3),
                DateTimeOffset.UtcNow.AddDays(expired ? -1 : 3), RandomNumberGenerator.GetBytes(16));
            using var attachedLeaf = publicLeaf.CopyWithPrivateKey(leafKey);
            // Schannel requires a provider-backed key; no certificate is added to a trust store.
            var tlsLeaf = X509CertificateLoader.LoadPkcs12(attachedLeaf.Export(X509ContentType.Pfx),
                null, X509KeyStorageFlags.Exportable);
            return (root, tlsLeaf);
        }
        finally { intermediate?.Dispose(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Real_TLS_preserves_expiry_hostname_and_negotiated_version(bool expired, bool wrongHostname)
    {
        var certificates = Certificates(wrongHostname ? "wrong.example.com" : "audit.example.com", expired);
        using var root = certificates.Root;
        using var leaf = certificates.Leaf;
        await using var server = new HttpFixture(_ => new(), leaf);
        var options = new AuditOptions();
        using var budget = new AuditBudget(options);
        var transport = new SafeTargetTransport(options, new FixtureResolver(_ => [TransportTests.PublicIp]),
            new FixtureConnector(server.Port), [root]);
        var result = await transport.FetchAsync(new("https://audit.example.com/"), HttpMethod.Get, budget, null, default);
        Assert.True(result.Tls is not null, string.Join("; ", server.Errors));
        if (expired || wrongHostname)
        {
            Assert.Equal("tls_validation_failed", result.ErrorCode);
            Assert.False(result.Tls.CertificateValid);
            Assert.Equal(expired, result.Tls.Expired);
            Assert.Equal(!wrongHostname, result.Tls.HostnameMatches);
            Assert.Empty(server.Requests);
        }
        else
        {
            Assert.Null(result.ErrorCode);
            Assert.True(result.Tls.CertificateValid);
            Assert.Contains(result.Tls.Protocol, new[] { "Tls12", "Tls13" });
            Assert.Single(server.Requests);
        }
    }

    [Fact]
    public async Task Offline_certificate_validation_cannot_fetch_an_internal_AIA_resource()
    {
        await using var aiaServer = new HttpFixture(_ => new());
        var certificates = Certificates("audit.example.com", aia: "http://127.0.0.1:" + aiaServer.Port + "/issuer.cer");
        Assert.Empty(aiaServer.Requests); // isolate import-time traffic from either TLS endpoint
        using var root = certificates.Root;
        using var leaf = certificates.Leaf;
        if (OperatingSystem.IsWindows())
        {
            // Schannel's fixture SERVER builds its own outgoing chain independently of the client.
            // Isolate the actual client chain policy here; full TLS egress is verified on Linux.
            using var chain = new X509Chain { ChainPolicy = CertificatePolicies.Offline([root]) };
            Assert.False(chain.Build(leaf));
            Assert.Empty(aiaServer.Requests);
            return;
        }
        await using var server = new HttpFixture(_ => new(), leaf);
        var options = new AuditOptions();
        using var budget = new AuditBudget(options);
        var transport = new SafeTargetTransport(options, new FixtureResolver(_ => [TransportTests.PublicIp]),
            new FixtureConnector(server.Port), [root]);
        var result = await transport.FetchAsync(new("https://audit.example.com/"), HttpMethod.Get, budget, null, default);
        Assert.Equal("tls_validation_failed", result.ErrorCode);
        Assert.False(result.Tls!.ChainTrusted);
        Assert.Empty(aiaServer.Requests);
        Assert.Empty(server.Requests);
    }
}
