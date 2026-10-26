using NearWebsiteAuditAgent.Contracts;

namespace NearWebsiteAuditAgent.Core;

internal static class TlsRedirectChecks
{
    public static void Evaluate(AuditObservation observation, CheckResults result)
    {
        var tlsFetch = observation.Https ?? observation.Main;
        var tls = tlsFetch.Tls;
        if (tls is not null)
        {
            if (tls.CertificateValid) result.Set("tls.https", CheckOutcome.Passed);
            else result.Fail("tls.https", Severity.High, "https_validation_failed", "HTTPS certificate validation failed.",
                "Provide a valid trusted certificate matching the requested hostname.");
            if (!tls.CertificatePresented) result.Set("tls.certificate", CheckOutcome.NotTested, "certificate_unavailable");
            else if (tls.CertificateValid) result.Set("tls.certificate", CheckOutcome.Passed);
            else result.Fail("tls.certificate", Severity.High, "invalid_certificate",
                $"Certificate validation failed: expired={tls.Expired}, notYetValid={tls.NotYetValid}, hostnameMatches={tls.HostnameMatches}, chainTrusted={tls.ChainTrusted}.",
                "Renew or replace the certificate and correct the hostname and trust chain.");
            if (tls.Protocol is "Tls12" or "Tls13") result.Set("tls.protocol", CheckOutcome.Passed);
            else if (tls.Protocol is not null)
                result.Fail("tls.protocol", Severity.High, "legacy_tls", "A legacy TLS protocol was negotiated.",
                    "Require a modern TLS protocol and update the server.");
        }
        else if (tlsFetch.ErrorCode == "connection_refused")
            result.Fail("tls.https", Severity.High, "https_unavailable", "The HTTPS connection was refused.",
                "Provide a reachable HTTPS endpoint.");
        var chain = observation.Main;
        if (chain.ErrorCode is "redirect_loop" or "redirect_limit" or "https_downgrade" or "target_restricted")
            result.Fail("redirects.chain", chain.ErrorCode == "target_restricted" ? Severity.High : Severity.Medium,
                chain.ErrorCode, "The redirect chain stopped: " + chain.ErrorCode + ".",
                "Use a short, public HTTPS redirect chain without loops or downgrades.");
        else if (chain.ErrorCode is null && chain.Response is not null) result.Set("redirects.chain", CheckOutcome.Passed);
        var upgrade = observation.HttpUpgrade;
        if (upgrade?.ErrorCode is null && upgrade?.Response is { } response)
        {
            if (response.Url.Scheme == "https") result.Set("redirects.upgrade", CheckOutcome.Passed);
            else result.Fail("redirects.upgrade", Severity.Medium, "http_not_upgraded", "HTTP does not redirect to HTTPS.",
                "Redirect HTTP requests to a valid HTTPS destination.");
        }
        else if (upgrade?.ErrorCode == "https_downgrade")
            result.Fail("redirects.upgrade", Severity.Medium, "https_downgrade", "An HTTPS to HTTP downgrade was observed.",
                "Keep the redirect chain on HTTPS.");
    }
}
