using System.Net;
using NearWebsiteAuditAgent.Core;

namespace NearWebsiteAuditAgent.Api;

internal static class ApiErrors
{
    public static Task WriteAsync(HttpContext context, int status, string code)
        => Results.Problem(statusCode: status, title: code, extensions: new Dictionary<string, object?> { ["code"] = code }).ExecuteAsync(context);
}
internal sealed class ApiBoundary(RequestDelegate next, ILogger<ApiBoundary> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.IsHttps && (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)))
        { await ApiErrors.WriteAsync(context, 426, "https_required"); return; }
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        { if (!context.Response.HasStarted) context.Response.StatusCode = 499; }
        catch (BadHttpRequestException error)
        {
            if (!context.Response.HasStarted) await ApiErrors.WriteAsync(context, error.StatusCode,
                error.StatusCode == 413 ? "input_too_large" : "invalid_input");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogError("api_error {Code}", error is AuditException audit ? audit.Code : "internal_error");
            if (!context.Response.HasStarted) await ApiErrors.WriteAsync(context, 500, "internal_error");
        }
    }
}
