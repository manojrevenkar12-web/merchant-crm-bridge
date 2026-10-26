using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Bridge.Api.Tests;

/// <summary>contracts/openapi.json (OpenAPI 3.1, whose schemas are JSON Schema 2020-12) as validators.</summary>
public sealed class OpenApi
{
    public static OpenApi Default { get; } = new(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "openapi.json")));

    private readonly JsonObject _doc;
    private readonly JsonNode _schemas;

    private OpenApi(string text)
    {
        _doc = JsonNode.Parse(text)!.AsObject();
        _schemas = _doc["components"]!["schemas"]!;
    }

    public IEnumerable<(string Method, string Path)> Operations =>
        _doc["paths"]!.AsObject().SelectMany(p => p.Value!.AsObject().Select(m => (m.Key.ToUpperInvariant(), p.Key)));

    public JsonNode Operation(string method, string path) => _doc["paths"]![path]![method.ToLowerInvariant()]!;

    public IEnumerable<string> Statuses(string method, string path) => Operation(method, path)["responses"]!.AsObject().Select(r => r.Key);

    /// <summary>Validates a body against the schema the contract declares for that status; returns the errors.</summary>
    public IReadOnlyList<string> Check(string method, string path, int status, string contentType, string body)
    {
        var response = Operation(method, path)["responses"]![status.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        if (response is null) return [$"status {status} is not in the contract for {method} {path}"];
        if (response["$ref"] is not null) return []; // 401/403 carry no body in the contract
        var content = response["content"]?.AsObject();
        if (content is null) return body.Length == 0 ? [] : [$"{method} {path} {status} declares no body but got one"];
        var media = contentType.Split(';')[0].Trim();
        if (!content.ContainsKey(media)) return [$"{method} {path} {status} returned {media}; contract has {string.Join(", ", content.Select(c => c.Key))}"];
        var schemaText = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$defs"] = _schemas.DeepClone(),
            ["allOf"] = new JsonArray(content[media]!["schema"]!.DeepClone()),
        }.ToJsonString().Replace("#/components/schemas/", "#/$defs/", StringComparison.Ordinal);
        var schema = JsonSchema.FromText(schemaText);
        using var json = JsonDocument.Parse(body);
        var result = schema.Evaluate(json.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid) return [];
        return (result.Details ?? []).Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}")).Distinct().Take(10).ToList();
    }
}
