using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NearWebsiteAuditAgent.Infrastructure;

public static class CertificatePolicies
{
    public static X509ChainPolicy Offline(IReadOnlyCollection<X509Certificate2>? trustAnchors = null)
    {
        var policy = new X509ChainPolicy
        {
            RevocationMode = X509RevocationMode.NoCheck,
            DisableCertificateDownloads = true,
            VerificationFlags = X509VerificationFlags.NoFlag
        };
        policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        if (trustAnchors is { Count: > 0 })
        {
            policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            foreach (var root in trustAnchors) policy.CustomTrustStore.Add(root);
        }
        return policy;
    }
}
