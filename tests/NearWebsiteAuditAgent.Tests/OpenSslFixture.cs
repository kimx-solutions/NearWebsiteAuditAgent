using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NearWebsiteAuditAgent.Tests;

internal sealed class OpenSslFixture : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _stdout;
    private readonly Task<string> _stderr;
    public int Port { get; }
    public OpenSslFixture(string directory, X509Certificate2 certificate)
    {
        var certPath = Path.Combine(directory, "fixture-cert.pem");
        var keyPath = Path.Combine(directory, "fixture-key.pem");
        File.WriteAllText(certPath, certificate.ExportCertificatePem());
        using var key = certificate.GetRSAPrivateKey()!;
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var executable = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "usr", "bin", "openssl.exe")
            : "openssl";
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "s_server", "-accept", "127.0.0.1:" + Port, "-cert", certPath, "-key", keyPath, "-quiet", "-www", "-no_cache" })
            start.ArgumentList.Add(argument);
        _process = Process.Start(start) ?? throw new InvalidOperationException("OpenSSL fixture could not start.");
        _stdout = _process.StandardOutput.ReadToEndAsync();
        _stderr = _process.StandardError.ReadToEndAsync();
    }
    public async Task ReadyAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            if (_process.HasExited) throw new InvalidOperationException("OpenSSL fixture failed: " + await _stderr);
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(IPAddress.Loopback, Port, timeout.Token); return; }
            catch (SocketException) { await Task.Delay(20, timeout.Token); }
        }
        throw new TimeoutException("OpenSSL fixture did not become ready.");
    }
    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync();
        await Task.WhenAll(_stdout, _stderr);
        _process.Dispose();
    }
}
