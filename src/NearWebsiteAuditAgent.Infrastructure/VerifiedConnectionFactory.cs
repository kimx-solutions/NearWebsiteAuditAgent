using System.Net;
using System.Net.Sockets;
using NearWebsiteAuditAgent.Core;

namespace NearWebsiteAuditAgent.Infrastructure;

public sealed class SystemHostResolver : IHostResolver
{
    public Task<IPAddress[]> ResolveAsync(string hostname, CancellationToken cancellationToken)
        => Dns.GetHostAddressesAsync(hostname, cancellationToken);
}
public sealed class SocketAddressConnector : IAddressConnector
{
    public async Task<ConnectedStream> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
            if (socket.RemoteEndPoint is not IPEndPoint remote) throw new AuditException("target_restricted");
            return new(new NetworkStream(socket, ownsSocket: true), remote.Address);
        }
        catch { socket.Dispose(); throw; }
    }
}
internal sealed class VerifiedConnectionFactory(AuditOptions options, IHostResolver resolver, IAddressConnector connector)
{
    public async Task<Stream> OpenAsync(DnsEndPoint endpoint, CancellationToken cancellationToken)
    {
        using var dnsTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        dnsTimeout.CancelAfter(TimeSpan.FromSeconds(options.DnsTimeoutSeconds));
        var addresses = await resolver.ResolveAsync(endpoint.Host, dnsTimeout.Token);
        if (addresses.Length is 0 or > 64) throw new AuditException("dns_failed");
        if (addresses.Any(ip => !PublicAddressPolicy.IsPublic(ip))) throw new AuditException("target_restricted");
        var address = addresses.OrderBy(ip => ip.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ThenBy(ip => ip.ToString(), StringComparer.Ordinal).First();
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(options.ConnectTimeoutSeconds));
        var connection = await connector.ConnectAsync(address, endpoint.Port, connectTimeout.Token);
        var peer = connection.RemoteAddress.IsIPv4MappedToIPv6 ? connection.RemoteAddress.MapToIPv4() : connection.RemoteAddress;
        var expected = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (PublicAddressPolicy.IsPublic(peer) && peer.Equals(expected)) return connection.Stream;
        await connection.Stream.DisposeAsync();
        throw new AuditException("target_restricted");
    }
}
