using System.Text.Json.Nodes;
using Json.Schema;

namespace Bridge.Core.Contracts;

/// <summary>The JSON Schemas in contracts/schemas, embedded in the assembly. Every event is validated
/// against the schema of its type and version before anything reads it.</summary>
public sealed class SchemaRegistry
{
    private readonly Dictionary<(string Type, int Version), JsonSchema> _schemas = [];

    public static SchemaRegistry Default { get; } = Load();

    public IEnumerable<(string Type, int Version)> Known => _schemas.Keys;

    private static SchemaRegistry Load()
    {
        var registry = new SchemaRegistry();
        var asm = typeof(SchemaRegistry).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("schemas/", StringComparison.Ordinal)))
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            var file = name["schemas/".Length..^".json".Length]; // e.g. platform.merchant.kyc_decided.v1
            var dot = file.LastIndexOf(".v", StringComparison.Ordinal);
            var type = file[..dot];
            var version = int.Parse(file[(dot + 2)..], System.Globalization.CultureInfo.InvariantCulture);
            registry._schemas[(type, version)] = JsonSchema.FromText(text);
        }
        return registry;
    }

    public bool Has(string type, int version) => _schemas.ContainsKey((type, version));

    /// <summary>Throws <see cref="ContractException"/> listing what is wrong.</summary>
    public void Validate(string type, int version, JsonNode? data)
    {
        if (!_schemas.TryGetValue((type, version), out var schema))
            throw new ContractException($"no schema for {type} v{version}");
        var element = data is null ? System.Text.Json.JsonSerializer.SerializeToElement<object?>(null) : System.Text.Json.JsonSerializer.SerializeToElement(data);
        var result = schema.Evaluate(element, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid) return;
        var errors = (result.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{(d.InstanceLocation.ToString() is { Length: > 0 } at ? at : "data")}: {e.Value}"))
            .Where(m => !m.Contains("did not match the required schema", StringComparison.Ordinal))
            .Take(5)
            .ToList();
        throw new ContractException($"{type} v{version} does not match its schema: " + (errors.Count > 0 ? string.Join("; ", errors) : "invalid"));
    }
}
