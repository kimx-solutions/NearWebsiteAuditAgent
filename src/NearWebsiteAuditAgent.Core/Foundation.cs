using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NearWebsiteAuditAgent.Contracts;

namespace NearWebsiteAuditAgent.Core;

public sealed class AuditException(string code, string? settingKey = null) : Exception(code)
{
    public string Code { get; } = code;
    public string? SettingKey { get; } = settingKey;
}
public static class AuditInput
{
    public static AuditRequest Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > 8192)
            throw new AuditException("invalid_input");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new AuditException("invalid_input");
            var properties = document.RootElement.EnumerateObject().ToArray();
            if (properties.Length != 1 || properties[0].Name != "url" ||
                properties[0].Value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(properties[0].Value.GetString())) throw new AuditException("invalid_input");
            return new AuditRequest(properties[0].Value.GetString()!);
        }
        catch (JsonException) { throw new AuditException("invalid_input"); }
    }
}
public static class TargetUri
{
    public static Uri Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 ||
            value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            uri.UserInfo.Length != 0 || uri.Port is not (80 or 443) || uri.Host.Contains('%'))
            throw new AuditException("invalid_input");
        var host = uri.IdnHost.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || host.Contains('%')) throw new AuditException("invalid_input");
        if (IPAddress.TryParse(host, out var address))
        {
            if (!PublicAddressPolicy.IsPublic(address)) throw new AuditException("target_restricted");
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var authority = value[(value.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/', '?', '#')[0];
                if (authority.Split(':')[0] != address.ToString()) throw new AuditException("invalid_input");
            }
        }
        else if (Uri.CheckHostName(host) != UriHostNameType.Dns || !host.Contains('.') ||
            new[] { ".localhost", ".local", ".internal", ".lan", ".home", ".invalid" }
                .Any(suffix => host.EndsWith(suffix, StringComparison.Ordinal)))
            throw new AuditException("target_restricted");
        return new UriBuilder(uri) { Host = host, Fragment = "" }.Uri;
    }
    public static bool SameOrigin(Uri a, Uri b)
        => a.Scheme == b.Scheme && a.IdnHost == b.IdnHost && a.Port == b.Port;
}
public static class PublicAddressPolicy
{
    private static readonly (IPAddress Network, int Prefix)[] DeniedV4 = [
        (IPAddress.Parse("0.0.0.0"), 8), (IPAddress.Parse("10.0.0.0"), 8),
        (IPAddress.Parse("100.64.0.0"), 10), (IPAddress.Parse("127.0.0.0"), 8),
        (IPAddress.Parse("169.254.0.0"), 16), (IPAddress.Parse("172.16.0.0"), 12),
        (IPAddress.Parse("192.0.0.0"), 24), (IPAddress.Parse("192.0.2.0"), 24),
        (IPAddress.Parse("192.88.99.0"), 24), (IPAddress.Parse("192.168.0.0"), 16),
        (IPAddress.Parse("198.18.0.0"), 15), (IPAddress.Parse("198.51.100.0"), 24),
        (IPAddress.Parse("203.0.113.0"), 24), (IPAddress.Parse("224.0.0.0"), 4),
        (IPAddress.Parse("240.0.0.0"), 4), (IPAddress.Parse("168.63.129.16"), 32)
    ];
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !DeniedV4.Any(range => InSubnet(address, range.Network, range.Prefix));
        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId == 0 &&
            InSubnet(address, IPAddress.Parse("2000::"), 3) &&
            !InSubnet(address, IPAddress.Parse("2001::"), 23) &&
            !InSubnet(address, IPAddress.Parse("2001:db8::"), 32) &&
            !InSubnet(address, IPAddress.Parse("2002::"), 16) &&
            !InSubnet(address, IPAddress.Parse("3fff::"), 20);
    }
    private static bool InSubnet(IPAddress address, IPAddress network, int prefix)
    {
        var bytes = address.GetAddressBytes();
        var subnet = network.GetAddressBytes();
        for (var i = 0; i < bytes.Length && prefix > 0; i++, prefix -= 8)
        {
            var mask = (byte)(0xff << Math.Max(0, 8 - prefix));
            if ((bytes[i] & mask) != (subnet[i] & mask)) return false;
        }
        return true;
    }
}
public static class Redaction
{
    public static string Url(Uri uri) => uri.GetLeftPart(UriPartial.Authority) + "/";
    public static string Resource(Uri uri)
        => Url(uri) + " [resource " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.PathAndQuery)))[..12].ToLowerInvariant() + "]";
    public static string Token(string value)
        => new(value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.').Take(64).ToArray());
}
public sealed record ScoreSummary(int? Score, string? Grade, bool Provisional);
public static class Scoring
{
    public static int Weight(Severity severity) => severity switch
    {
        Severity.Critical => 25,
        Severity.High => 15,
        Severity.Medium => 8,
        Severity.Low => 3,
        _ => 0
    };
    public static ScoreSummary Calculate(IReadOnlyList<CheckResult> checks, IReadOnlyList<AuditFinding> findings)
    {
        if (!checks.Any(c => c.Outcome is CheckOutcome.Passed or CheckOutcome.Failed)) return new(null, null, true);
        var score = Math.Max(0, 100 - findings.GroupBy(f => f.CheckId, StringComparer.Ordinal)
            .Sum(group => group.Max(f => Weight(f.Severity))));
        var provisional = checks.Any(c => c.Outcome == CheckOutcome.NotTested || c.Completeness == Completeness.Partial);
        var grade = score switch { >= 90 => "A", >= 80 => "B", >= 70 => "C", >= 60 => "D", _ => "F" };
        return new(score, provisional ? null : grade, provisional);
    }
    public static Coverage GetCoverage(IReadOnlyList<CheckResult> checks)
    {
        var passed = checks.Count(c => c.Outcome == CheckOutcome.Passed);
        var failed = checks.Count(c => c.Outcome == CheckOutcome.Failed);
        return new(checks.Count, passed, failed, checks.Count(c => c.Outcome == CheckOutcome.NotTested),
            checks.Count(c => c.Outcome == CheckOutcome.NotApplicable), passed + failed);
    }
}
