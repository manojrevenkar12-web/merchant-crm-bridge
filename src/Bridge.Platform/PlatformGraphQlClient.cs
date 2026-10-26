using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Bridge.Core.Contracts;
using Bridge.Core.Ports;
using Bridge.Dataverse;

namespace Bridge.Platform;

public sealed class PlatformOptions
{
    public Uri GraphQlEndpoint { get; set; } = new("http://localhost:5200/graphql");

    /// <summary>The platform API's app ID URI; the bridge's managed identity gets a token for it.</summary>
    public string Scope { get; set; } = "api://payments-platform/.default";

    public int BatchSize { get; set; } = 50;
}

/// <summary>Reads the platform's view of merchants over GraphQL, in batches, with an Entra token.</summary>
public sealed class PlatformGraphQlClient(HttpClient http, ITokenProvider tokens, PlatformOptions options) : IPlatformApi
{
    private const string Fields = "merchantId platformAccountId kyc { decision reasonCode version } status { status reason version } risk { chargebackRatio riskBand version }";

    public async Task<MerchantSnapshot?> GetSnapshotAsync(string merchantId, CancellationToken ct)
    {
        var data = await QueryAsync($"query($id: String!) {{ merchant(id: $id) {{ {Fields} }} }}", new JsonObject { ["id"] = merchantId }, ct).ConfigureAwait(false);
        return data["merchant"] is JsonObject m ? Read(m) : null;
    }

    public async Task<IReadOnlyDictionary<string, MerchantSnapshot>> GetSnapshotsAsync(IReadOnlyCollection<string> merchantIds, CancellationToken ct)
    {
        var result = new Dictionary<string, MerchantSnapshot>(StringComparer.Ordinal);
        foreach (var chunk in merchantIds.Chunk(options.BatchSize))
        {
            var data = await QueryAsync($"query($ids: [String!]!) {{ merchants(ids: $ids) {{ {Fields} }} }}",
                new JsonObject { ["ids"] = new JsonArray(chunk.Select(id => (JsonNode)id).ToArray()) }, ct).ConfigureAwait(false);
            foreach (var m in data["merchants"]!.AsArray()) { var s = Read(m!.AsObject()); result[s.MerchantId] = s; }
        }
        return result;
    }

    private async Task<JsonObject> QueryAsync(string query, JsonObject variables, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.GraphQlEndpoint)
        {
            Content = JsonContent.Create(new JsonObject { ["query"] = query, ["variables"] = variables }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(options.Scope, ct).ConfigureAwait(false));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if ((int)response.StatusCode is 429 or >= 500)
            throw new TransientException($"platform API {(int)response.StatusCode}", response.Headers.RetryAfter?.Delta);
        response.EnsureSuccessStatusCode();
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))!.AsObject();
        if (body["errors"] is JsonArray { Count: > 0 } errors)
            throw new InvalidOperationException("platform API: " + string.Join("; ", errors.Select(e => (string?)e?["message"])));
        return body["data"]!.AsObject();
    }

    private static MerchantSnapshot Read(JsonObject m) => new(
        (string)m["merchantId"]!,
        (string?)m["platformAccountId"],
        m["kyc"] is JsonObject k ? new SnapshotKyc((string)k["decision"]!, (string?)k["reasonCode"], (long)k["version"]!) : null,
        m["status"] is JsonObject s ? new SnapshotStatus((string)s["status"]!, (string?)s["reason"], (long)s["version"]!) : null,
        m["risk"] is JsonObject r ? new SnapshotRisk((decimal)r["chargebackRatio"]!, (string)r["riskBand"]!, (long)r["version"]!) : null);
}
