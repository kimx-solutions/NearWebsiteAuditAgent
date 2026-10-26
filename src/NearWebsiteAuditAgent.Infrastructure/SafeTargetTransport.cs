using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NearWebsiteAuditAgent.Core;

namespace NearWebsiteAuditAgent.Infrastructure;

public interface IHostResolver
{
    Task<IPAddress[]> ResolveAsync(string hostname, CancellationToken cancellationToken);
}
public sealed record ConnectedStream(Stream Stream, IPAddress RemoteAddress);
public interface IAddressConnector
{
    Task<ConnectedStream> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken);
}
public sealed class SafeTargetTransport(AuditOptions options, IHostResolver resolver,
    IAddressConnector connector, IReadOnlyCollection<X509Certificate2>? trustAnchors = null) : ISafeTargetTransport
{
    private readonly VerifiedConnectionFactory _connections = new(options, resolver, connector);
    public async Task<FetchObservation> FetchAsync(Uri target, HttpMethod method, AuditBudget budget,
        Uri? scopeOrigin, CancellationToken cancellationToken)
    {
        using var lease = await budget.EnterAsync(cancellationToken);
        var hops = new List<RedirectHop>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        TlsObservation? tls = null;
        try
        {
            if (method != HttpMethod.Get && method != HttpMethod.Head) throw new AuditException("invalid_input");
            var current = TargetUri.Parse(target.AbsoluteUri);
            for (var redirects = 0; ; redirects++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!visited.Add(current.AbsoluteUri)) return new(null, hops, tls, "redirect_loop");
                budget.TakeAttempt();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
                using var handler = CreateHandler(value => tls = value, () => tls);
                using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                using var request = new HttpRequestMessage(method, current)
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
                request.Headers.ConnectionClose = true;
                request.Headers.UserAgent.ParseAdd("NearWebsiteAuditAgent/1.0");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var headers = response.Headers.Concat(response.Content.Headers).GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.SelectMany(h => h.Value).ToArray(), StringComparer.OrdinalIgnoreCase);
                var headerBytes = headers.Sum(h => Encoding.UTF8.GetByteCount(h.Key) + h.Value.Sum(Encoding.UTF8.GetByteCount) + 4);
                if (headerBytes > options.MaxResponseHeaderBytes)
                {
                    budget.Limit("max_response_header_bytes");
                    return new(null, hops, tls, "budget_exceeded");
                }
                var status = (int)response.StatusCode;
                hops.Add(new(current, status));
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    if (response.Headers.Location is null) return new(null, hops, tls, "invalid_redirect");
                    if (redirects >= options.MaxRedirects) { budget.Limit("max_redirects"); return new(null, hops, tls, "redirect_limit"); }
                    var next = TargetUri.Parse(new Uri(current, response.Headers.Location).AbsoluteUri);
                    if (current.Scheme == "https" && next.Scheme == "http") return new(null, hops, tls, "https_downgrade");
                    if (scopeOrigin is not null && !TargetUri.SameOrigin(scopeOrigin, next))
                        return new(null, hops, tls, "out_of_scope_redirect");
                    current = next;
                    continue;
                }
                var body = method == HttpMethod.Head ? "" : await TargetBodyReader.ReadAsync(response.Content, budget, timeout.Token);
                return new(new(current, status, headers, body, response.Content.Headers.ContentType?.MediaType), hops, tls);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(null, hops, tls, "request_timeout"); }
        catch (Exception error) when (error is AuditException or HttpRequestException or IOException or SocketException or AuthenticationException)
        { return new(null, hops, tls, ErrorCode(error)); }
    }

    private SocketsHttpHandler CreateHandler(Action<TlsObservation> record, Func<TlsObservation?> currentTls)
    {
        var chainPolicy = CertificatePolicies.Offline(trustAnchors);
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            Credentials = null,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            MaxResponseHeadersLength = Math.Max(1, (options.MaxResponseHeaderBytes + 1023) / 1024),
            ConnectTimeout = TimeSpan.FromSeconds(options.ConnectTimeoutSeconds),
            ConnectCallback = async (context, ct) => await _connections.OpenAsync(context.DnsEndPoint, ct),
            SslOptions = new SslClientAuthenticationOptions
            {
                CertificateChainPolicy = chainPolicy,
                EnabledSslProtocols = SslProtocols.None,
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    if (certificate is null)
                    {
                        record(new(false, false, false, false, false, false, null));
                        return false;
                    }
                    using var copy = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                    var now = DateTime.UtcNow;
                    record(new(true, errors == SslPolicyErrors.None, copy.NotAfter.ToUniversalTime() < now,
                        copy.NotBefore.ToUniversalTime() > now,
                        (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0,
                        (errors & SslPolicyErrors.RemoteCertificateChainErrors) == 0, null));
                    return errors == SslPolicyErrors.None;
                }
            },
            PlaintextStreamFilter = (context, _) =>
            {
                if (context.PlaintextStream is SslStream ssl && currentTls() is { } observed)
                    record(observed with { Protocol = ssl.SslProtocol.ToString() });
                return ValueTask.FromResult(context.PlaintextStream);
            }
        };
    }
    private static string ErrorCode(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is AuditException audit) return audit.Code;
            if (current is AuthenticationException) return "tls_validation_failed";
            if (current is SocketException { SocketErrorCode: SocketError.ConnectionRefused }) return "connection_refused";
            if (current is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData }) return "dns_failed";
        }
        return "network_failed";
    }
}
