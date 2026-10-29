using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NearWebsiteAuditAgent.Contracts;
using NearWebsiteAuditAgent.Core;

namespace NearWebsiteAuditAgent.Api;

internal static class AuditEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, IAuditRunner runner, AuditOptions options,
        IHostApplicationLifetime lifetime)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
        AuditRequest input;
        try { input = await ReadInputAsync(context.Request, cancellation.Token); }
        catch (ApiInputException error) { return Problem(error.Status, error.Code); }
        var result = await runner.RunAsync(input, cancellation.Token);
        cancellation.Token.ThrowIfCancellationRequested();
        if (result.Status is not (AuditStatus.Succeeded or AuditStatus.Partial or AuditStatus.Rejected or AuditStatus.Failed))
            return Problem(500, "internal_error");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, ReportJson.Options);
        if (bytes.Length > options.MaxReportBytes) return Problem(503, "report_too_large");
        var status = result.Status switch
        {
            AuditStatus.Succeeded or AuditStatus.Partial => 200,
            AuditStatus.Rejected => 422,
            AuditStatus.Failed when result.Errors.Any(error => error.Code == "audit_timeout") => 504,
            AuditStatus.Failed => 502,
            _ => 500
        };
        return Results.Content(Encoding.UTF8.GetString(bytes), "application/json", Encoding.UTF8, status);
    }
    private static IResult Problem(int status, string code)
        => Results.Problem(statusCode: status, title: code, extensions: new Dictionary<string, object?> { ["code"] = code });
    private static async Task<AuditRequest> ReadInputAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) ||
            !mediaType.MediaType!.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
            (mediaType.CharSet is { Length: > 0 } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)) ||
            request.Headers.ContentEncoding.Count > 0) throw new ApiInputException(415, "unsupported_media_type");
        if (request.ContentLength > 8192) throw new ApiInputException(413, "input_too_large");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var buffer = new MemoryStream();
        var chunk = new byte[8193];
        try
        {
            while (true)
            {
                var read = await request.Body.ReadAsync(chunk.AsMemory(0, 8193 - (int)buffer.Length), timeout.Token);
                if (read == 0) break;
                buffer.Write(chunk, 0, read);
                if (buffer.Length > 8192) throw new ApiInputException(413, "input_too_large");
            }
            return AuditInput.Parse(new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ApiInputException(408, "request_body_timeout"); }
        catch (Exception error) when (error is AuditException or DecoderFallbackException)
        { throw new ApiInputException(400, "invalid_input"); }
    }
    private sealed class ApiInputException(int status, string code) : Exception(code)
    {
        public int Status { get; } = status;
        public string Code { get; } = code;
    }
}
