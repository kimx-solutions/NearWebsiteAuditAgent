# Website audit

Target: https://audit.example.com/
Status: Succeeded
Score: 64 / Grade D

## Coverage

Tested 22/22; passed 15, failed 7, not tested 0, not applicable 0.
Pages parsed: 1; requests/probes: 8; links selected/eligible: 2/2; excluded: 2; inconclusive: 0.

## Checks

| Check | Result | Completeness | Reason |
| --- | --- | --- | --- |
| tls.https | Passed | Complete |  |
| tls.certificate | Passed | Complete |  |
| tls.protocol | Passed | Complete |  |
| headers.hsts | Failed | Complete |  |
| headers.csp | Failed | Complete |  |
| headers.framing | Failed | Complete |  |
| headers.nosniff | Failed | Complete |  |
| headers.referrer | Failed | Complete |  |
| headers.permissions | Failed | Complete |  |
| cookies.secure | Passed | Complete |  |
| cookies.httponly | Passed | Complete |  |
| cookies.samesite | Passed | Complete |  |
| redirects.upgrade | Passed | Complete |  |
| redirects.chain | Passed | Complete |  |
| seo.title | Passed | Complete |  |
| seo.description | Passed | Complete |  |
| seo.h1 | Passed | Complete |  |
| seo.canonical | Passed | Complete |  |
| seo.robots\_meta | Passed | Complete |  |
| seo.robots\_txt | Passed | Complete |  |
| seo.sitemap | Passed | Complete |  |
| seo.links | Failed | Complete |  |

- cookies.secure evidence: sid — Secure=True; HttpOnly=True; SameSite=Lax; lifetime=nonpersistent; purpose=unknown
- cookies.httponly evidence: sid — Secure=True; HttpOnly=True; SameSite=Lax; lifetime=nonpersistent; purpose=unknown
- cookies.samesite evidence: sid — Secure=True; HttpOnly=True; SameSite=Lax; lifetime=nonpersistent; purpose=unknown

## Findings

### Medium — missing\_csp

An enforced CSP header is missing.
- Evidence: target — An enforced CSP header is missing.
- Recommendation: Define an enforced Content-Security-Policy appropriate for the application.

### Medium — missing\_framing\_policy

No supported restrictive framing policy was observed.
- Evidence: target — No supported restrictive framing policy was observed.
- Recommendation: Use CSP frame-ancestors or a supported X-Frame-Options policy.

### Medium — missing\_hsts

A positive valid HSTS max-age is missing.
- Evidence: target — A positive valid HSTS max-age is missing.
- Recommendation: Configure a positive Strict-Transport-Security max-age after validating HTTPS deployment.

### Low — missing\_nosniff

A valid nosniff header is missing.
- Evidence: target — A valid nosniff header is missing.
- Recommendation: Set X-Content-Type-Options: nosniff.

### Low — missing\_permissions\_policy

Permissions-Policy is missing.
- Evidence: target — Permissions-Policy is missing.
- Recommendation: Restrict browser features that this application does not need.

### Low — unsafe\_referrer\_policy

A supported conservative Referrer-Policy is missing.
- Evidence: target — A supported conservative Referrer-Policy is missing.
- Recommendation: Choose a policy such as strict-origin-when-cross-origin appropriate for the application.

### Low — broken\_link

A selected link returned HTTP 404.
- Evidence: https://audit.example.com/ \[resource 5d70bb3fbc6b\] — A selected link returned HTTP 404.
- Recommendation: Correct or remove the broken link.

## Limits and errors

- Only the submitted page is parsed; linked pages are not crawled.
- JavaScript is not executed. Online certificate revocation is not checked.
- Unknown cookie authentication purpose is not assumed safe.

Rules: rules-v1; scoring: score-v1; schema: 1.0.
