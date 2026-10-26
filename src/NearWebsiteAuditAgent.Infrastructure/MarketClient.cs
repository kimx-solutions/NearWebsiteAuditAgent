using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NearWebsiteAuditAgent.Core;

namespace NearWebsiteAuditAgent.Infrastructure;

public sealed class MarketClient : IMarketClient
{
    private readonly HttpClient _client;
    private readonly MarketOptions _options;
    private static readonly HashSet<string> Statuses = ["in_progress", "submitted", "accepted", "disputed", "dispute_resolved", "cancelled"];
    public MarketClient(HttpClient client, MarketOptions options)
    {
        if (client.BaseAddress?.AbsoluteUri != "https://market.near.ai/" || !options.AgentApiToken.StartsWith("aat_", StringComparison.Ordinal) ||
            options.AgentApiToken.Length > 4096 || options.AgentApiToken.Any(c => c <= 32 || c >= 127))
            throw new AuditException("configuration_invalid");
        _client = client;
        _options = options;
    }
    public Task<IReadOnlyList<MarketAssignment>> PollAsync(CancellationToken cancellationToken)
        => ListAsync("in_progress", null, cancellationToken);
    public async Task<MarketAssignment?> FindAsync(Guid assignmentId, CancellationToken cancellationToken)
        => (await ListAsync("all", assignmentId, cancellationToken)).FirstOrDefault(a => a.AssignmentId == assignmentId);

    private async Task<IReadOnlyList<MarketAssignment>> ListAsync(string status, Guid? requestedId, CancellationToken cancellationToken)
    {
        const int pageSize = 50;
        var assignments = new Dictionary<Guid, MarketAssignment>();
        for (var page = 0; page < _options.MaxPages; page++)
        {
            var bytes = await SendAsync(HttpMethod.Get, $"v1/agents/me/assignments?status={status}&limit={pageSize}&offset={page * (pageSize - 1)}",
                null, _options.MaxReadAttempts, false, cancellationToken);
            try
            {
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
                var envelope = Unique(document.RootElement);
                var rows = Required(envelope, "assignments", JsonValueKind.Array);
                foreach (var row in rows.EnumerateArray())
                {
                    var item = Unique(row);
                    var assignment = ParseAssignment(Required(item, "assignment", JsonValueKind.Object));
                    var job = Unique(Required(item, "job", JsonValueKind.Object));
                    if (GuidValue(job, "jobId") != assignment.JobId) throw ContractChanged();
                    _ = Text(job, "title");
                    _ = Text(job, "status");
                    var tags = Required(job, "tags", JsonValueKind.Array);
                    if (tags.EnumerateArray().Any(tag => tag.ValueKind != JsonValueKind.String)) throw ContractChanged();
                    _ = Required(job, "attachments", JsonValueKind.Array);
                    var feedback = ParseFeedback(item);
                    assignment = assignment with
                    {
                        Brief = Text(job, "description"),
                        JobKind = Text(job, "jobKind"),
                        Feedback = feedback.Body,
                        FeedbackTruncated = feedback.Truncated,
                        FeedbackFromBuyer = feedback.Buyer
                    };
                    if (!assignments.TryGetValue(assignment.AssignmentId, out var prior) || assignment.UpdatedAt >= prior.UpdatedAt)
                        assignments[assignment.AssignmentId] = assignment;
                }
                if (requestedId is { } id && assignments.ContainsKey(id)) return assignments.Values.ToArray();
                if (rows.GetArrayLength() < pageSize) return assignments.Values.OrderBy(a => a.AssignmentId).ToArray();
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
            { throw ContractChanged(); }
        }
        throw new MarketException("market_page_limit");
    }
    public async Task<DateTimeOffset> StartAsync(Guid assignmentId, CancellationToken cancellationToken)
    {
        var bytes = await SendAsync(HttpMethod.Post, $"v1/assignments/{assignmentId:D}/start", null,
            _options.MaxStartAttempts, false, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return Timestamp(Unique(document.RootElement), "startedAt");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        { throw ContractChanged(); }
    }
    public async Task SubmitAsync(Guid assignmentId, string markdown, CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(markdown) is 0 or > 262144) throw new MarketException("invalid_deliverable");
        var body = JsonSerializer.SerializeToUtf8Bytes(new { deliverableMarkdown = markdown });
        byte[] bytes;
        try { bytes = await SendAsync(HttpMethod.Post, $"v1/assignments/{assignmentId:D}/submit", body, 1, true, cancellationToken); }
        catch (MarketException error) when (error.Code == "market_contract_changed")
        { throw new MarketException("submission_outcome_unknown", outcomeUnknown: true); }
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var returned = ParseAssignment(document.RootElement);
            if (returned.AssignmentId != assignmentId || returned.Status != "submitted") throw ContractChanged();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or MarketException)
        { throw new MarketException("submission_outcome_unknown", outcomeUnknown: true); }
    }

    private async Task<byte[]> SendAsync(HttpMethod method, string path, byte[]? body, int attempts,
        bool submission, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            TimeSpan? retryDelay = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AgentApiToken);
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new("application/json");
            }
            try
            {
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.IsSuccessStatusCode) return await ReadBoundedAsync(response.Content, timeout.Token);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new MarketException("market_auth_failed", pauseProcessing: true);
                var transient = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                    (int)response.StatusCode >= 500;
                if (submission && transient) throw new MarketException("submission_outcome_unknown", outcomeUnknown: true);
                if (!transient) throw new MarketException("market_request_rejected");
                retryDelay = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !submission) { throw; }
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
            {
                if (submission) throw new MarketException("submission_outcome_unknown", outcomeUnknown: true);
            }
            if (attempt + 1 >= attempts) throw new MarketException("market_unavailable");
            var delay = retryDelay ?? TimeSpan.FromMilliseconds(1000 * (attempt + 1) + Random.Shared.Next(101));
            // Caller cancellation/SLA always bounds Retry-After; do not sleep for an untrusted unbounded duration.
            if (delay > TimeSpan.FromMinutes(1)) throw new MarketException("market_rate_limited");
            await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, cancellationToken);
        }
        throw new MarketException("market_unavailable");
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        const int maximum = 4194304;
        if (content.Headers.ContentLength > maximum) throw ContractChanged();
        using var buffer = new MemoryStream();
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var bytes = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(bytes, cancellationToken);
            if (count == 0) return buffer.ToArray();
            if (buffer.Length + count > maximum) throw ContractChanged();
            buffer.Write(bytes, 0, count);
        }
    }

    private static MarketAssignment ParseAssignment(JsonElement element)
    {
        var fields = Unique(element);
        var status = Text(fields, "status");
        if (!Statuses.Contains(status)) throw ContractChanged();
        _ = Text(fields, "escrowAmount"); _ = Text(fields, "escrowToken");
        if (Required(fields, "slaSeconds", JsonValueKind.Number).GetInt32() <= 0) throw ContractChanged();
        _ = Required(fields, "acceptanceWindowDays", JsonValueKind.Number).GetInt32();
        _ = Timestamp(fields, "createdAt");
        return new(GuidValue(fields, "assignmentId"), GuidValue(fields, "jobId"), GuidValue(fields, "workerAgentId"),
            status, Timestamp(fields, "slaDeadlineAt"), Timestamp(fields, "updatedAt"), "",
            OptionalTimestamp(fields, "startedAt"), OptionalTimestamp(fields, "submittedAt"));
    }
    private static (string? Body, bool Truncated, bool Buyer) ParseFeedback(Dictionary<string, JsonElement> fields)
    {
        if (!fields.TryGetValue("latestMessage", out var message) || message.ValueKind == JsonValueKind.Null) return (null, false, false);
        var values = Unique(message);
        _ = Timestamp(values, "createdAt"); _ = Text(values, "origin");
        _ = Required(values, "attachments", JsonValueKind.Array);
        var truncated = values.GetValueOrDefault("truncated");
        if (truncated.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw ContractChanged();
        return (Text(values, "body"), truncated.GetBoolean(), Text(values, "senderSide") == "buyer");
    }
    private static Dictionary<string, JsonElement> Unique(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw ContractChanged();
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in element.EnumerateObject()) if (!fields.TryAdd(field.Name, field.Value)) throw ContractChanged();
        return fields;
    }
    private static JsonElement Required(Dictionary<string, JsonElement> fields, string key, JsonValueKind kind)
    {
        if (!fields.TryGetValue(key, out var value) || value.ValueKind != kind) throw ContractChanged();
        return value;
    }
    private static string Text(Dictionary<string, JsonElement> fields, string key)
        => Required(fields, key, JsonValueKind.String).GetString()!;
    private static Guid GuidValue(Dictionary<string, JsonElement> fields, string key)
    {
        if (!Guid.TryParse(Text(fields, key), out var value) || value == Guid.Empty) throw ContractChanged();
        return value;
    }
    private static DateTimeOffset Timestamp(Dictionary<string, JsonElement> fields, string key)
    {
        var element = Required(fields, key, JsonValueKind.String);
        var text = element.GetString()!;
        if (!(text.EndsWith('Z') || System.Text.RegularExpressions.Regex.IsMatch(text, @"[+-]\d{2}:\d{2}$")) ||
            !element.TryGetDateTimeOffset(out var value)) throw ContractChanged();
        return value.ToUniversalTime();
    }
    private static DateTimeOffset? OptionalTimestamp(Dictionary<string, JsonElement> fields, string key)
        => !fields.TryGetValue(key, out var value) || value.ValueKind == JsonValueKind.Null ? null : Timestamp(fields, key);
    private static MarketException ContractChanged() => new("market_contract_changed", pauseProcessing: true);
}
