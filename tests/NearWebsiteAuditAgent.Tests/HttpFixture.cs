using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NearWebsiteAuditAgent.Infrastructure;

namespace NearWebsiteAuditAgent.Tests;

internal sealed record FixtureRequest(string Method, string Path, string Headers);
internal sealed record FixtureResponse(int Status = 200, string Body = "ok",
    IReadOnlyDictionary<string, string>? Headers = null, bool Chunked = false);

internal sealed class HttpFixture : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _clients = [];
    private readonly Task _accept;
    private readonly Func<FixtureRequest, CancellationToken, Task<FixtureResponse>> _handler;
    private readonly X509Certificate2? _certificate;
    public ConcurrentQueue<FixtureRequest> Requests { get; } = new();
    public ConcurrentQueue<string> Errors { get; } = new();
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public HttpFixture(Func<FixtureRequest, FixtureResponse> handler, X509Certificate2? certificate = null)
        : this((request, _) => Task.FromResult(handler(request)), certificate) { }
    public HttpFixture(Func<FixtureRequest, CancellationToken, Task<FixtureResponse>> handler, X509Certificate2? certificate = null)
    {
        _handler = handler;
        _certificate = certificate;
        _listener.Start();
        _accept = AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
                _clients.Add(ServeAsync(await _listener.AcceptTcpClientAsync(_stop.Token)));
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }
    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                Stream stream = client.GetStream();
                if (_certificate is not null)
                {
                    var ssl = new SslStream(stream, false);
                    using var certificateContext = new ContextOwner(_certificate);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificateContext = certificateContext.Context,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                    }, _stop.Token);
                    stream = ssl;
                }
                await using (stream)
                {
                    using var memory = new MemoryStream();
                    var one = new byte[1];
                    while (memory.Length < 32768 && await stream.ReadAsync(one, _stop.Token) != 0)
                    {
                        memory.WriteByte(one[0]);
                        if (memory.Length >= 4 && Encoding.ASCII.GetString(memory.GetBuffer(), (int)memory.Length - 4, 4) == "\r\n\r\n") break;
                    }
                    if (memory.Length == 0) return;
                    var headers = Encoding.ASCII.GetString(memory.ToArray());
                    var startLine = headers.Split("\r\n")[0].Split(' ');
                    if (startLine.Length < 2) return;
                    var request = new FixtureRequest(startLine[0], startLine[1], headers);
                    Requests.Enqueue(request);
                    var response = await _handler(request, _stop.Token);
                    var body = Encoding.UTF8.GetBytes(response.Body);
                    var builder = new StringBuilder("HTTP/1.1 " + response.Status + " Fixture\r\nConnection: close\r\n");
                    if (response.Chunked) builder.Append("Transfer-Encoding: chunked\r\n");
                    else builder.Append("Content-Length: " + body.Length + "\r\n");
                    if (response.Headers is not null)
                        foreach (var header in response.Headers) builder.Append(header.Key + ": " + header.Value + "\r\n");
                    builder.Append("\r\n");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), _stop.Token);
                    if (request.Method != "HEAD")
                    {
                        if (response.Chunked)
                        {
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(body.Length.ToString("X") + "\r\n"), _stop.Token);
                            await stream.WriteAsync(body, _stop.Token);
                            await stream.WriteAsync("\r\n0\r\n\r\n"u8.ToArray(), _stop.Token);
                        }
                        else await stream.WriteAsync(body, _stop.Token);
                    }
                }
            }
            catch (IOException error) { Errors.Enqueue(error.ToString()); }
            catch (AuthenticationException error) { Errors.Enqueue(error.ToString()); }
            catch (OperationCanceledException) { }
        }
    }
    // The context stays offline: the test server itself must not trigger AIA downloads.
    private sealed class ContextOwner(X509Certificate2 certificate) : IDisposable
    {
        public SslStreamCertificateContext Context { get; } = SslStreamCertificateContext.Create(certificate, additionalCertificates: null, offline: true);
        public void Dispose() { }
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accept;
        await Task.WhenAll(_clients);
        _stop.Dispose();
    }
}

internal sealed class FixtureResolver(Func<string, IPAddress[]> resolve) : IHostResolver
{
    public int Calls { get; private set; }
    public Task<IPAddress[]> ResolveAsync(string hostname, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return Task.FromResult(resolve(hostname));
    }
}
internal sealed class FixtureConnector(int fixturePort, IPAddress? reportedPeer = null) : IAddressConnector
{
    public int Calls { get; private set; }
    public List<IPAddress> RequestedAddresses { get; } = [];
    public async Task<ConnectedStream> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        Calls++;
        RequestedAddresses.Add(address);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, fixturePort, cancellationToken);
            return new(new NetworkStream(socket, ownsSocket: true), reportedPeer ?? address);
        }
        catch { socket.Dispose(); throw; }
    }
}
