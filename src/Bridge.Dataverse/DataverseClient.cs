using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Bridge.Dataverse;

public sealed class DataverseOptions
{
    /// <summary>The environment URL, e.g. https://contoso.crm4.dynamics.com/.</summary>
    public Uri EnvironmentUrl { get; set; } = new("http://localhost:5100/");

    /// <summary>Throttling waits up to this long are taken inline; longer ones are handed to the caller
    /// (a Kafka consumer must pause its partition rather than block).</summary>
    public TimeSpan MaxInlineRetryAfter { get; set; } = TimeSpan.FromSeconds(2);

    public int MaxInlineRetries { get; set; } = 3;

    /// <summary>The OAuth scope for the environment: "{environment URL}/.default".</summary>
    public string Scope => EnvironmentUrl.GetLeftPart(UriPartial.Authority) + "/.default";
}

/// <summary>A row reference in a URL: primary id or alternate key.</summary>
public readonly record struct RowKey(string Segment)
{
    public static RowKey Id(Guid id) => new(id.ToString());

    public static RowKey Alternate(params (string Column, object Value)[] values) =>
        new(string.Join(",", values.Select(v => $"{v.Column}={Literal(v.Value)}")));

    public static string Literal(object value) => value switch
    {
        string s => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'",
        Guid g => g.ToString(),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}

public sealed record Page(IReadOnlyList<JsonObject> Rows, string? NextLink);

public sealed record WriteResponse(Guid? Id, string? ETag);

/// <summary>One operation in a $batch changeset.</summary>
public sealed record BatchOperation(string Method, string RelativeUrl, JsonObject? Body, IReadOnlyDictionary<string, string>? Headers = null);

public sealed record BatchResult(int Status, JsonObject? Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// The Dataverse Web API v9.2 over HttpClient. Every call carries the OData headers and a bearer token;
/// 429 responses honour Retry-After (short waits inline, long ones surface as an exception the caller
/// schedules around); errors carry Dataverse's hex code so callers can tell a concurrency conflict from
/// a duplicate key or a plug-in rule.
/// </summary>
public sealed class DataverseClient(HttpClient http, ITokenProvider tokens, DataverseOptions options, TimeProvider clock)
{
    private Uri Api => new(options.EnvironmentUrl, "api/data/v9.2/");

    public async Task<(JsonObject? Row, string? ETag)> GetAsync(string set, RowKey key, string? select, CancellationToken ct)
    {
        var url = $"{set}({key.Segment})" + (select is null ? "" : "?$select=" + select);
        using var response = await SendAsync(() => Request(HttpMethod.Get, url), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return (null, null);
        await EnsureAsync(response, ct).ConfigureAwait(false);
        var row = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))!.AsObject();
        return (row, response.Headers.ETag?.ToString() ?? (string?)row["@odata.etag"]);
    }

    public async Task<Page> QueryAsync(string set, string? filter, string? select, string? orderBy, int? top, int pageSize, CancellationToken ct)
    {
        var parts = new List<string>();
        if (filter is not null) parts.Add("$filter=" + Uri.EscapeDataString(filter));
        if (select is not null) parts.Add("$select=" + select);
        if (orderBy is not null) parts.Add("$orderby=" + Uri.EscapeDataString(orderBy));
        if (top is not null) parts.Add("$top=" + top.Value.ToString(CultureInfo.InvariantCulture));
        return await QueryUrlAsync(set + (parts.Count > 0 ? "?" + string.Join("&", parts) : ""), pageSize, ct).ConfigureAwait(false);
    }

    /// <summary>Follows an @odata.nextLink (absolute) or a relative query.</summary>
    public async Task<Page> QueryUrlAsync(string url, int pageSize, CancellationToken ct)
    {
        using var response = await SendAsync(() =>
        {
            var r = Request(HttpMethod.Get, url);
            r.Headers.Add("Prefer", $"odata.maxpagesize={pageSize}");
            return r;
        }, ct).ConfigureAwait(false);
        await EnsureAsync(response, ct).ConfigureAwait(false);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))!.AsObject();
        var rows = body["value"]!.AsArray().Select(n => n!.AsObject()).ToList();
        return new Page(rows, (string?)body["@odata.nextLink"]);
    }

    public async Task<WriteResponse> CreateAsync(string set, JsonObject body, CancellationToken ct)
    {
        using var response = await SendAsync(() => Request(HttpMethod.Post, set, body), ct).ConfigureAwait(false);
        await EnsureAsync(response, ct).ConfigureAwait(false);
        return Written(response);
    }

    /// <summary>PATCH: an upsert unless <paramref name="ifMatch"/> ("*" = update only, an ETag = only that
    /// version) or <paramref name="ifNoneMatch"/> ("*" = create only) say otherwise.</summary>
    public async Task<WriteResponse> PatchAsync(string set, RowKey key, JsonObject body, string? ifMatch, string? ifNoneMatch, CancellationToken ct)
    {
        using var response = await SendAsync(() =>
        {
            var r = Request(HttpMethod.Patch, $"{set}({key.Segment})", body);
            if (ifMatch is not null) r.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            if (ifNoneMatch is not null) r.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
            return r;
        }, ct).ConfigureAwait(false);
        await EnsureAsync(response, ct).ConfigureAwait(false);
        return Written(response);
    }

    public async Task DeleteAsync(string set, RowKey key, string? ifMatch, CancellationToken ct)
    {
        using var response = await SendAsync(() =>
        {
            var r = Request(HttpMethod.Delete, $"{set}({key.Segment})");
            if (ifMatch is not null) r.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            return r;
        }, ct).ConfigureAwait(false);
        await EnsureAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<JsonObject?> InvokeBoundActionAsync(string set, RowKey key, string action, JsonObject? parameters, CancellationToken ct)
    {
        using var response = await SendAsync(() => Request(HttpMethod.Post, $"{set}({key.Segment})/Microsoft.Dynamics.CRM.{action}", parameters ?? []), ct).ConfigureAwait(false);
        await EnsureAsync(response, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text)!.AsObject();
    }

    /// <summary>Runs the operations as one changeset: all commit or none. Throws the failing operation's
    /// error as a <see cref="DataverseRequestException"/>.</summary>
    public async Task<IReadOnlyList<BatchResult>> ChangesetAsync(IReadOnlyList<BatchOperation> operations, CancellationToken ct)
    {
        var batchBoundary = "batch_" + Guid.NewGuid().ToString("N");
        var changesetBoundary = "changeset_" + Guid.NewGuid().ToString("N");
        var payload = BuildBatch(operations, batchBoundary, changesetBoundary);
        using var response = await SendAsync(() =>
        {
            var r = Request(HttpMethod.Post, "$batch");
            r.Content = new StringContent(payload, Encoding.UTF8);
            r.Content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/mixed; boundary={batchBoundary}");
            return r;
        }, ct).ConfigureAwait(false);
        await EnsureAsync(response, ct).ConfigureAwait(false);
        var boundary = response.Content.Headers.ContentType?.Parameters.FirstOrDefault(p => p.Name == "boundary")?.Value?.Trim('"')
            ?? throw new DataverseRequestException(500, "batch", "batch response without boundary");
        var results = BatchParser.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), boundary);
        var failed = results.FirstOrDefault(r => r.Status >= 400);
        if (failed is not null)
        {
            var error = failed.Body?["error"];
            throw new DataverseRequestException(failed.Status, (string?)error?["code"] ?? "", (string?)error?["message"] ?? "changeset failed",
                failed.Status == 429 && failed.Headers.TryGetValue("Retry-After", out var ra) ? TimeSpan.FromSeconds(double.Parse(ra, CultureInfo.InvariantCulture)) : null);
        }
        return results;
    }

    private string BuildBatch(IReadOnlyList<BatchOperation> operations, string batchBoundary, string changesetBoundary)
    {
        var sb = new StringBuilder();
        sb.Append("--").Append(batchBoundary).Append("\r\n");
        sb.Append("Content-Type: multipart/mixed; boundary=").Append(changesetBoundary).Append("\r\n\r\n");
        var contentId = 1;
        foreach (var op in operations)
        {
            sb.Append("--").Append(changesetBoundary).Append("\r\n");
            sb.Append("Content-Type: application/http\r\nContent-Transfer-Encoding: binary\r\n");
            sb.Append("Content-ID: ").Append(contentId++.ToString(CultureInfo.InvariantCulture)).Append("\r\n\r\n");
            sb.Append(op.Method).Append(' ').Append(new Uri(Api, op.RelativeUrl)).Append(" HTTP/1.1\r\n");
            sb.Append("Content-Type: application/json\r\n");
            if (op.Headers is not null) foreach (var (k, v) in op.Headers) sb.Append(k).Append(": ").Append(v).Append("\r\n");
            sb.Append("\r\n");
            if (op.Body is not null) sb.Append(op.Body.ToJsonString());
            sb.Append("\r\n");
        }
        sb.Append("--").Append(changesetBoundary).Append("--\r\n");
        sb.Append("--").Append(batchBoundary).Append("--\r\n");
        return sb.ToString();
    }

    private HttpRequestMessage Request(HttpMethod method, string relative, JsonObject? body = null)
    {
        var uri = relative.StartsWith("http", StringComparison.Ordinal) ? new Uri(relative) : new Uri(Api, relative);
        var r = new HttpRequestMessage(method, uri);
        r.Headers.Add("OData-MaxVersion", "4.0");
        r.Headers.Add("OData-Version", "4.0");
        r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null) r.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return r;
    }

    // Sends with a token; waits out short throttling inline (Retry-After), surfaces long throttling and
    // 5xx after the inline retries as DataverseRequestException with IsTransient.
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        var token = await tokens.GetTokenAsync(options.Scope, ct).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            using var request = build();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                if (attempt >= options.MaxInlineRetries) throw new DataverseRequestException(503, "network", ex.Message);
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), clock, ct).ConfigureAwait(false);
                continue;
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                if (wait > options.MaxInlineRetryAfter || attempt >= options.MaxInlineRetries)
                {
                    response.Dispose();
                    throw new DataverseRequestException(429, "0x80072322", "service protection limit", wait);
                }
                response.Dispose();
                await Task.Delay(wait, clock, ct).ConfigureAwait(false);
                continue;
            }
            if ((int)response.StatusCode is 502 or 503 or 504 && attempt < options.MaxInlineRetries)
            {
                response.Dispose();
                await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1)), clock, ct).ConfigureAwait(false);
                continue;
            }
            return response;
        }
    }

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotModified) return;
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        string code = "", message = text;
        try
        {
            var error = JsonNode.Parse(text)?["error"];
            code = (string?)error?["code"] ?? "";
            message = (string?)error?["message"] ?? text;
        }
        catch (System.Text.Json.JsonException)
        {
            // not JSON: keep the text
        }
        throw new DataverseRequestException((int)response.StatusCode, code, message, response.Headers.RetryAfter?.Delta);
    }

    private static WriteResponse Written(HttpResponseMessage response)
    {
        Guid? id = null;
        if (response.Headers.TryGetValues("OData-EntityId", out var values))
        {
            var url = values.First();
            var open = url.LastIndexOf('(');
            if (open > 0 && Guid.TryParse(url[(open + 1)..^1], out var g)) id = g;
        }
        return new WriteResponse(id, response.Headers.ETag?.ToString());
    }
}

/// <summary>Reads a multipart/mixed $batch response (with nested changesets) into one result per operation.</summary>
public static class BatchParser
{
    public static IReadOnlyList<BatchResult> Parse(string body, string boundary)
    {
        var results = new List<BatchResult>();
        foreach (var part in Split(body, boundary))
        {
            var (headers, content) = SplitHeaders(part);
            if (headers.TryGetValue("Content-Type", out var ct) && ct.StartsWith("multipart/mixed", StringComparison.OrdinalIgnoreCase))
            {
                var inner = ct[(ct.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase) + 9)..].Trim().Trim('"');
                results.AddRange(Parse(content, inner));
            }
            else
            {
                results.Add(ParseHttp(content));
            }
        }
        return results;
    }

    private static IEnumerable<string> Split(string body, string boundary)
    {
        var text = body.Replace("\r\n", "\n", StringComparison.Ordinal);
        var marker = "--" + boundary;
        foreach (var chunk in text.Split(marker))
        {
            var c = chunk.Trim('\n');
            if (c.Length == 0 || c.StartsWith("--", StringComparison.Ordinal)) continue;
            yield return c;
        }
    }

    private static (Dictionary<string, string> Headers, string Content) SplitHeaders(string part)
    {
        var end = part.IndexOf("\n\n", StringComparison.Ordinal);
        var head = end < 0 ? part : part[..end];
        var content = end < 0 ? "" : part[(end + 2)..];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in head.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return (headers, content);
    }

    private static BatchResult ParseHttp(string content)
    {
        var (headerBlock, body) = SplitHeaders(content.TrimStart('\n'));
        var statusLine = content.TrimStart('\n').Split('\n')[0];
        var status = int.Parse(statusLine.Split(' ')[1], CultureInfo.InvariantCulture);
        headerBlock.Remove(statusLine.Split(':')[0]);
        var trimmed = body.Trim();
        return new BatchResult(status, trimmed.StartsWith('{') ? JsonNode.Parse(trimmed)!.AsObject() : null, headerBlock);
    }
}
