using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Bridge.Core.Contracts;

/// <summary>A CloudEvents 1.0 event in structured JSON mode: the whole event is the Kafka value, the
/// merchant id is the Kafka key (so one merchant's events stay in order on one partition).</summary>
public sealed record CloudEvent
{
    [JsonPropertyName("specversion")] public string SpecVersion { get; init; } = "1.0";
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }

    /// <summary>The merchant id.</summary>
    [JsonPropertyName("subject")] public required string Subject { get; init; }

    [JsonPropertyName("time")] public required DateTimeOffset Time { get; init; }
    [JsonPropertyName("datacontenttype")] public string DataContentType { get; init; } = "application/json";
    [JsonPropertyName("dataschema")] public string? DataSchema { get; init; }

    /// <summary>Extension: the producer's per-merchant sequence (monotonic per merchant and producer).</summary>
    [JsonPropertyName("sequence")] public long Sequence { get; init; }

    /// <summary>Extension: version of the data schema.</summary>
    [JsonPropertyName("schemaversion")] public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("data")] public required JsonNode Data { get; init; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static string SchemaUri(string type, int version) => $"contracts/schemas/{type}.v{version}.json";

    /// <summary>Parses an event; throws <see cref="ContractException"/> for anything that is not one.</summary>
    public static CloudEvent Parse(string json)
    {
        CloudEvent? ce;
        try
        {
            ce = JsonSerializer.Deserialize<CloudEvent>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new ContractException("not a CloudEvent: " + ex.Message, ex);
        }
        if (ce is null) throw new ContractException("empty message");
        if (ce.SpecVersion != "1.0") throw new ContractException($"unsupported specversion {ce.SpecVersion}");
        if (string.IsNullOrWhiteSpace(ce.Id) || string.IsNullOrWhiteSpace(ce.Type) || string.IsNullOrWhiteSpace(ce.Subject))
            throw new ContractException("id, type and subject are required");
        return ce;
    }
}

/// <summary>A message that breaks the contract: retrying cannot help, it goes to the dead-letter topic.</summary>
public sealed class ContractException : Exception
{
    public ContractException(string message) : base(message) { }
    public ContractException(string message, Exception inner) : base(message, inner) { }
    public ContractException() { }
}
