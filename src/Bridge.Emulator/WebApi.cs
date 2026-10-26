using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Bridge.Emulator;

/// <summary>The HTTP surface: OData URLs, headers, paging, the token endpoint, service protection and
/// $batch with changesets. Responses follow the Dataverse Web API (status codes, OData-EntityId, ETag,
/// error bodies with a hex code).</summary>
public sealed class WebApi(Engine engine, TimeProvider clock)
{
    public const string Root = "/api/data/v9.2/";
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _requests = [];
    private readonly object _throttleGate = new();
    private long _served;
    private long _throttled;

    public long Served => Interlocked.Read(ref _served);
    public long Throttled => Interlocked.Read(ref _throttled);

    public sealed record Response(int Status, JsonNode? Body, Dictionary<string, string> Headers)
    {
        public static Response Empty(int status) => new(status, null, []);
    }

    // --- authentication ---------------------------------------------------------------------------

    public Caller? Authenticate(string? authorization)
    {
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
        var token = authorization["Bearer ".Length..].Trim();
        if (token == "emu.app." + engine.Options.IntegrationClientId)
            return new Caller(engine.Options.IntegrationUserId, "# merchant-crm-bridge", true);
        // emu.user.<guid>.<name>: a person (sales, operations) in tests and drills.
        var parts = token.Split('.', 4);
        if (parts.Length == 4 && parts[0] == "emu" && parts[1] == "user" && Guid.TryParse(parts[2], out var id))
            return new Caller(id, parts[3], false);
        return null;
    }

    public static IResult TokenEndpoint(HttpContext http, EmulatorOptions options)
    {
        var form = http.Request.Form;
        if (form["grant_type"] != "client_credentials") return Results.BadRequest(new { error = "unsupported_grant_type" });
        if (form["client_id"] != options.IntegrationClientId || form["client_secret"] != options.IntegrationClientSecret)
            return Results.Json(new { error = "invalid_client", error_description = "AADSTS7000215: Invalid client secret provided." }, statusCode: 401);
        var scope = form["scope"].ToString();
        if (!scope.EndsWith("/.default", StringComparison.Ordinal)) return Results.BadRequest(new { error = "invalid_scope" });
        return Results.Json(new { token_type = "Bearer", expires_in = 3599, access_token = "emu.app." + options.IntegrationClientId });
    }

    // --- service protection -----------------------------------------------------------------------

    private TimeSpan? Throttle(Caller caller, int cost)
    {
        lock (_throttleGate)
        {
            var now = clock.GetUtcNow();
            if (!_requests.TryGetValue(caller.UserId, out var q)) _requests[caller.UserId] = q = new Queue<DateTimeOffset>();
            while (q.Count > 0 && q.Peek() <= now - engine.Options.ThrottleWindow) q.Dequeue();
            if (q.Count + cost > engine.Options.ThrottleLimit)
            {
                var wait = q.Count > 0 ? q.Peek() + engine.Options.ThrottleWindow - now : engine.Options.ThrottleWindow;
                return wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
            }
            for (var i = 0; i < cost; i++) q.Enqueue(now);
            return null;
        }
    }

    public void ResetThrottle()
    {
        lock (_throttleGate) _requests.Clear();
    }

    // --- request handling -------------------------------------------------------------------------

    public async Task HandleAsync(HttpContext http)
    {
        Response response;
        var caller = Authenticate(http.Request.Headers.Authorization);
        if (caller is null)
        {
            response = Error(new DataverseException(401, DataverseException.Unauthorized, "Bearer token is missing or not valid for this environment."));
            await WriteAsync(http.Response, response).ConfigureAwait(false);
            return;
        }

        var path = http.Request.Path.Value![Root.Length..];
        var isBatch = path == "$batch";
        string? body = null;
        if (http.Request.ContentLength is > 0 || http.Request.Headers.TransferEncoding.Count > 0 || isBatch)
        {
            using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        var cost = isBatch ? Math.Max(1, CountBatchOperations(body ?? "")) : 1;
        if (Throttle(caller, cost) is { } retryAfter)
        {
            Interlocked.Increment(ref _throttled);
            var r = Error(new DataverseException(429, DataverseException.Throttled,
                $"Number of requests exceeded the limit of {engine.Options.ThrottleLimit} over time window of {(int)engine.Options.ThrottleWindow.TotalSeconds} seconds."));
            r.Headers["Retry-After"] = Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            await WriteAsync(http.Response, r).ConfigureAwait(false);
            return;
        }

        await Latency().ConfigureAwait(false);
        Interlocked.Increment(ref _served);

        if (isBatch)
        {
            if (engine.Options.FailNextBatches > 0)
            {
                engine.Options.FailNextBatches--;
                await WriteAsync(http.Response, Error(new DataverseException(503, "0x80044151", "Service unavailable (injected fault)."))).ConfigureAwait(false);
                return;
            }
            var contentType = MediaTypeHeaderValue.Parse(http.Request.ContentType);
            var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value!;
            var (text, responseBoundary) = await BatchAsync(caller, body ?? "", boundary).ConfigureAwait(false);
            http.Response.StatusCode = 200;
            http.Response.ContentType = $"multipart/mixed; boundary={responseBoundary}";
            await http.Response.WriteAsync(text).ConfigureAwait(false);
            return;
        }

        var headers = http.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        response = await ExecuteAsync(caller, http.Request.Method, path + http.Request.QueryString.Value, headers, body, transaction: null).ConfigureAwait(false);
        await WriteAsync(http.Response, response).ConfigureAwait(false);
    }

    private async Task Latency()
    {
        var o = engine.Options;
        if (o.MaxLatency <= TimeSpan.Zero) return;
        var ms = Random.Shared.Next((int)o.MinLatency.TotalMilliseconds, (int)o.MaxLatency.TotalMilliseconds + 1);
        await Task.Delay(ms).ConfigureAwait(false);
    }

    private static async Task WriteAsync(HttpResponse http, Response response)
    {
        http.StatusCode = response.Status;
        foreach (var (k, v) in response.Headers) http.Headers[k] = v;
        http.Headers["OData-Version"] = "4.0";
        if (response.Body is not null)
        {
            http.ContentType = "application/json; odata.metadata=minimal";
            await http.WriteAsync(response.Body.ToJsonString()).ConfigureAwait(false);
        }
    }

    public static Response Error(DataverseException ex) =>
        new(ex.Status, new JsonObject { ["error"] = new JsonObject { ["code"] = ex.Code, ["message"] = ex.Message } }, []);

    /// <summary>Runs one operation. With <paramref name="transaction"/> it joins a changeset; otherwise it
    /// is its own transaction.</summary>
    public async Task<Response> ExecuteAsync(Caller caller, string method, string pathAndQuery, IReadOnlyDictionary<string, string> headers, string? body, Store.Transaction? transaction)
    {
        var own = transaction is null;
        if (own) await engine.Store.Lock.WaitAsync().ConfigureAwait(false);
        var tx = transaction ?? new Store.Transaction(engine.Store);
        try
        {
            var response = Dispatch(caller, method, pathAndQuery, headers, body, tx);
            return response;
        }
        catch (DataverseException ex)
        {
            if (own) tx.Rollback();
            if (!own) throw;
            return Error(ex);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            if (own) tx.Rollback();
            var dv = DataverseException.Invalid("Error identified in Payload provided by the user: " + ex.Message);
            if (!own) throw dv;
            return Error(dv);
        }
        finally
        {
            if (own) engine.Store.Lock.Release();
        }
    }

    private Response Dispatch(Caller caller, string method, string pathAndQuery, IReadOnlyDictionary<string, string> headers, string? body, Store.Transaction tx)
    {
        var q = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        var path = Uri.UnescapeDataString(q < 0 ? pathAndQuery : pathAndQuery[..q]);
        var query = QueryHelpers.ParseQuery(q < 0 ? "" : pathAndQuery[q..]);

        if (path == "WhoAmI" && method == "GET")
            return new Response(200, new JsonObject { ["UserId"] = caller.UserId.ToString(), ["BusinessUnitId"] = Guid.Empty.ToString() }, []);

        const string action = "/Microsoft.Dynamics.CRM.";
        var actionAt = path.IndexOf(action, StringComparison.Ordinal);
        if (actionAt > 0)
        {
            var (aset, aref) = ParseSegment(path[..actionAt]);
            var name = path[(actionAt + action.Length)..];
            if (method != "POST" || aset != "accounts" || aref is null || name != "mcb_RequestOnboarding")
                throw new DataverseException(404, "0x80060888", $"Resource not found for the segment '{name}'.");
            return new Response(200, engine.RequestOnboarding(tx, caller, aref), []);
        }

        var (set, reference) = ParseSegment(path);
        var table = engine.Store.Metadata.BySet.TryGetValue(set, out var t) ? t : throw new DataverseException(404, "0x80060888", $"Resource not found for the segment '{set}'.");
        var select = query.TryGetValue("$select", out var sel) ? sel.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
        var returnRepresentation = headers.TryGetValue("Prefer", out var prefer) && prefer.Contains("return=representation", StringComparison.Ordinal);

        switch (method)
        {
            case "GET" when reference is null:
            {
                var rows = engine.Query(set, query.TryGetValue("$filter", out var f) ? f.ToString() : null, query.TryGetValue("$orderby", out var ob) ? ob.ToString() : null);
                var skip = query.TryGetValue("$skiptoken", out var st) ? int.Parse(st.ToString(), CultureInfo.InvariantCulture) : 0;
                var top = query.TryGetValue("$top", out var tp) ? int.Parse(tp.ToString(), CultureInfo.InvariantCulture) : int.MaxValue;
                var pageSize = 5000;
                if (prefer is not null && prefer.Contains("odata.maxpagesize=", StringComparison.Ordinal))
                    pageSize = int.Parse(prefer[(prefer.IndexOf("odata.maxpagesize=", StringComparison.Ordinal) + 18)..].Split(',')[0].Trim(), CultureInfo.InvariantCulture);
                var limit = Math.Min(top, rows.Count);
                var page = rows.Take(limit).Skip(skip).Take(pageSize).ToList();
                var value = new JsonArray(page.Select(r => (JsonNode)engine.ToJson(table, r, select)).ToArray());
                var result = new JsonObject { ["@odata.context"] = $"{engine.Options.BaseUrl}$metadata#{set}", ["value"] = value };
                if (skip + page.Count < limit)
                {
                    var next = QueryHelpers.AddQueryString(engine.Options.BaseUrl + set, query
                        .Where(kv => kv.Key != "$skiptoken")
                        .Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value.ToString()))
                        .Append(new KeyValuePair<string, string?>("$skiptoken", (skip + page.Count).ToString(CultureInfo.InvariantCulture))));
                    result["@odata.nextLink"] = next;
                }
                return new Response(200, result, []);
            }
            case "GET":
            {
                var (_, row) = engine.Retrieve(set, reference!);
                if (row is null) throw new DataverseException(404, DataverseException.NotFound, $"{table.LogicalName} With Id = {Describe(reference!)} Does Not Exist");
                if (headers.TryGetValue("If-None-Match", out var inm) && inm == Engine.ETag(row)) return Response.Empty(304);
                var json = engine.ToJson(table, row, select);
                json["@odata.context"] = $"{engine.Options.BaseUrl}$metadata#{set}/$entity";
                return new Response(200, json, new() { ["ETag"] = Engine.ETag(row) });
            }
            case "POST" when reference is null:
            {
                var values = engine.ReadBody(table, JsonNode.Parse(body ?? "{}")!.AsObject());
                var created = engine.Create(tx, caller, set, values);
                return Written(table, set, created, returnRepresentation, select);
            }
            case "PATCH" when reference is not null:
            {
                var values = engine.ReadBody(table, JsonNode.Parse(body ?? "{}")!.AsObject());
                headers.TryGetValue("If-Match", out var ifMatch);
                headers.TryGetValue("If-None-Match", out var ifNoneMatch);
                var written = engine.Patch(tx, caller, set, reference, values, ifMatch, ifNoneMatch);
                return Written(table, set, written, returnRepresentation, select);
            }
            case "DELETE" when reference is not null:
            {
                headers.TryGetValue("If-Match", out var ifMatch);
                engine.Delete(tx, caller, set, reference, ifMatch);
                return Response.Empty(204);
            }
            default:
                throw new DataverseException(405, "0x80060888", $"{method} is not supported on this resource.");
        }
    }

    private Response Written(TableMeta table, string set, Engine.WriteResult w, bool representation, string[] select)
    {
        var id = (Guid)w.Row.Values[table.PrimaryId]!;
        var headers = new Dictionary<string, string>
        {
            ["OData-EntityId"] = $"{engine.Options.BaseUrl}{set}({id})",
            ["ETag"] = Engine.ETag(w.Row),
        };
        if (!representation) return new Response(204, null, headers);
        return new Response(w.Created ? 201 : 200, engine.ToJson(table, w.Row, select), headers);
    }

    private static string Describe(RowRef r) => r.Id?.ToString() ?? string.Join(",", r.Keys!.Select(k => $"{k.Key}={k.Value}"));

    /// <summary>accounts, accounts(guid), accounts(mcb_merchantid='M-000001'), mcb_x(a='1',b=2).</summary>
    public (string Set, RowRef? Ref) ParseSegment(string segment)
    {
        var open = segment.IndexOf('(', StringComparison.Ordinal);
        if (open < 0) return (segment, null);
        if (!segment.EndsWith(')')) throw DataverseException.Invalid($"Bad segment {segment}.");
        var set = segment[..open];
        var inner = segment[(open + 1)..^1];
        if (Guid.TryParse(inner, out var id)) return (set, new RowRef(id, null));
        var table = engine.Store.Metadata.BySet.TryGetValue(set, out var t) ? t : throw new DataverseException(404, "0x80060888", $"Resource not found for the segment '{set}'.");
        var keys = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var part in SplitKeys(inner))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            var name = part[..eq].Trim();
            var raw = part[(eq + 1)..].Trim();
            if (!table.Columns.TryGetValue(name, out var col)) throw DataverseException.Invalid($"'{name}' is not a column of {table.LogicalName}.");
            keys[name] = raw.StartsWith('\'') ? raw[1..^1].Replace("''", "'", StringComparison.Ordinal)
                : col.Type == "bigint" ? long.Parse(raw, CultureInfo.InvariantCulture)
                : col.Type is "int" or "choice" ? int.Parse(raw, CultureInfo.InvariantCulture)
                : raw;
        }
        return (set, new RowRef(null, keys));
    }

    private static IEnumerable<string> SplitKeys(string s)
    {
        var inQuote = false;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\'') inQuote = !inQuote;
            else if (s[i] == ',' && !inQuote) { yield return s[start..i]; start = i + 1; }
        }
        yield return s[start..];
    }

    // --- $batch -----------------------------------------------------------------------------------

    private static int CountBatchOperations(string body) =>
        body.Split('\n').Count(l => l.StartsWith("GET ", StringComparison.Ordinal) || l.StartsWith("POST ", StringComparison.Ordinal)
            || l.StartsWith("PATCH ", StringComparison.Ordinal) || l.StartsWith("DELETE ", StringComparison.Ordinal));

    private sealed record Operation(string Method, string Url, Dictionary<string, string> Headers, string? Body, string? ContentId);

    private async Task<(string Text, string Boundary)> BatchAsync(Caller caller, string body, string boundary)
    {
        var parts = await ReadPartsAsync(body, boundary).ConfigureAwait(false);
        var responseBoundary = "batchresponse_" + Guid.NewGuid().ToString("N");
        var sb = new StringBuilder();
        foreach (var (contentType, content, _) in parts)
        {
            if (contentType.StartsWith("multipart/mixed", StringComparison.OrdinalIgnoreCase))
            {
                var csBoundary = HeaderUtilities.RemoveQuotes(MediaTypeHeaderValue.Parse(contentType).Boundary).Value!;
                var ops = (await ReadPartsAsync(content, csBoundary).ConfigureAwait(false)).Select(p => ParseOperation(p.Content, p.ContentId)).ToList();
                var (failed, text) = await ChangesetAsync(caller, ops).ConfigureAwait(false);
                sb.Append("--").Append(responseBoundary).Append("\r\n").Append(text);
                if (failed) break; // Dataverse stops at the first failure unless odata.continue-on-error
            }
            else
            {
                var op = ParseOperation(content, null);
                var r = await ExecuteAsync(caller, op.Method, Relative(op.Url), op.Headers, op.Body, transaction: null).ConfigureAwait(false);
                sb.Append("--").Append(responseBoundary).Append("\r\nContent-Type: application/http\r\nContent-Transfer-Encoding: binary\r\n\r\n").Append(HttpText(r));
                if (r.Status >= 400) break;
            }
        }
        sb.Append("--").Append(responseBoundary).Append("--\r\n");
        return (sb.ToString(), responseBoundary);
    }

    private async Task<(bool Failed, string Text)> ChangesetAsync(Caller caller, List<Operation> ops)
    {
        var csBoundary = "changesetresponse_" + Guid.NewGuid().ToString("N");
        await engine.Store.Lock.WaitAsync().ConfigureAwait(false);
        var tx = new Store.Transaction(engine.Store);
        var responses = new List<(string? ContentId, Response Response)>();
        try
        {
            foreach (var op in ops)
                responses.Add((op.ContentId, await ExecuteAsync(caller, op.Method, Relative(op.Url), op.Headers, op.Body, tx).ConfigureAwait(false)));
        }
        catch (DataverseException ex)
        {
            tx.Rollback();
            return (true, "Content-Type: application/http\r\nContent-Transfer-Encoding: binary\r\n\r\n" + HttpText(Error(ex)));
        }
        finally
        {
            engine.Store.Lock.Release();
        }
        var sb = new StringBuilder($"Content-Type: multipart/mixed; boundary={csBoundary}\r\n\r\n");
        foreach (var (contentId, r) in responses)
        {
            sb.Append("--").Append(csBoundary).Append("\r\nContent-Type: application/http\r\nContent-Transfer-Encoding: binary\r\n");
            if (contentId is not null) sb.Append("Content-ID: ").Append(contentId).Append("\r\n");
            sb.Append("\r\n").Append(HttpText(r));
        }
        sb.Append("--").Append(csBoundary).Append("--\r\n");
        return (false, sb.ToString());
    }

    private string Relative(string url)
    {
        var at = url.IndexOf(Root, StringComparison.Ordinal);
        return at >= 0 ? url[(at + Root.Length)..] : url.TrimStart('/');
    }

    private static string HttpText(Response r)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {r.Status} {ReasonPhrase(r.Status)}\r\n");
        foreach (var (k, v) in r.Headers) sb.Append(k).Append(": ").Append(v).Append("\r\n");
        if (r.Body is not null)
        {
            sb.Append("Content-Type: application/json; odata.metadata=minimal\r\n\r\n").Append(r.Body.ToJsonString()).Append("\r\n");
        }
        else
        {
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK", 201 => "Created", 204 => "No Content", 304 => "Not Modified", 400 => "Bad Request", 401 => "Unauthorized",
        403 => "Forbidden", 404 => "Not Found", 412 => "Precondition Failed", 429 => "Too Many Requests", _ => ((HttpStatusCode)status).ToString(),
    };

    private static async Task<List<(string ContentType, string Content, string? ContentId)>> ReadPartsAsync(string body, string boundary)
    {
        var reader = new MultipartReader(boundary, new MemoryStream(Encoding.UTF8.GetBytes(body)));
        var parts = new List<(string, string, string?)>();
        while (await reader.ReadNextSectionAsync().ConfigureAwait(false) is { } section)
        {
            using var sr = new StreamReader(section.Body, Encoding.UTF8);
            var content = await sr.ReadToEndAsync().ConfigureAwait(false);
            section.Headers!.TryGetValue("Content-ID", out var cid);
            parts.Add((section.ContentType ?? "application/http", content, cid.Count > 0 ? cid.ToString() : null));
        }
        return parts;
    }

    private static Operation ParseOperation(string content, string? contentId)
    {
        var text = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var headerEnd = text.IndexOf("\n\n", StringComparison.Ordinal);
        var head = headerEnd < 0 ? text : text[..headerEnd];
        var body = headerEnd < 0 ? null : text[(headerEnd + 2)..].Trim();
        var lines = head.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var requestLine = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return new Operation(requestLine[0], requestLine[1], headers, string.IsNullOrEmpty(body) ? null : body, contentId);
    }
}
