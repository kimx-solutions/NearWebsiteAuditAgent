using System.Text;
using NearWebsiteAuditAgent.Core;

namespace NearWebsiteAuditAgent.Infrastructure;

internal static class TargetBodyReader
{
    public static async Task<string> ReadAsync(HttpContent content, AuditBudget budget, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > budget.Options.MaxResponseBytes)
        {
            budget.Limit("max_response_bytes");
            throw new AuditException("budget_exceeded");
        }
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(chunk.AsMemory(0,
                (int)Math.Min(chunk.Length, budget.Options.MaxResponseBytes - buffer.Length + 1)), cancellationToken);
            if (count == 0) break;
            if (buffer.Length + count > budget.Options.MaxResponseBytes)
            {
                budget.Limit("max_response_bytes");
                throw new AuditException("budget_exceeded");
            }
            budget.AddBytes(count);
            buffer.Write(chunk, 0, count);
        }
        var encoding = Encoding.UTF8;
        if (content.Headers.ContentType?.CharSet is { Length: > 0 } charset)
        {
            try { encoding = Encoding.GetEncoding(charset.Trim('"')); }
            catch (ArgumentException) { throw new AuditException("unsupported_charset"); }
        }
        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
