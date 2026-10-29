using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace NearWebsiteAuditAgent.Api;

public sealed class ApiKeyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, IOptions<ApiKeyOptions> key) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var values = Request.Headers.Authorization;
        if (values.Count != 1 || values[0] is not { Length: <= 280 } value ||
            !AuthenticationHeaderValue.TryParse(value, out var header) || !header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
            header.Parameter is not { Length: >= 32 and <= 256 } supplied ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(key.Value.Key))))
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "api-client")], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(identity), Scheme.Name)));
    }
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return ApiErrors.WriteAsync(Context, 401, "unauthorized");
    }
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) => ApiErrors.WriteAsync(Context, 403, "forbidden");
}
