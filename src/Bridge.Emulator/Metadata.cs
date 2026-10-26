using System.Text.Json;

namespace Bridge.Emulator;

public sealed record ColumnMeta(string Name, string Type, int? MaxLength, string? Choice, string Owner);

public sealed record KeyMeta(string Name, string[] Attributes);

public sealed record TableMeta(string LogicalName, string EntitySet, string PrimaryId, string PrimaryName, IReadOnlyDictionary<string, ColumnMeta> Columns, IReadOnlyList<KeyMeta> Keys)
{
    public bool HasColumn(string name) => Columns.ContainsKey(name) || name == PrimaryId || SystemColumns.Contains(name);

    public static readonly HashSet<string> SystemColumns = new(StringComparer.Ordinal)
    {
        "createdon", "modifiedon", "_createdby_value", "_modifiedby_value", "versionnumber",
    };
}

/// <summary>The tables, columns, keys and choices from dataverse/model.json.</summary>
public sealed class Metadata
{
    public IReadOnlyDictionary<string, TableMeta> BySet { get; }
    public IReadOnlyDictionary<string, TableMeta> ByLogicalName { get; }
    public IReadOnlyDictionary<string, HashSet<int>> Choices { get; }

    private Metadata(List<TableMeta> tables, Dictionary<string, HashSet<int>> choices)
    {
        BySet = tables.ToDictionary(t => t.EntitySet, StringComparer.Ordinal);
        ByLogicalName = tables.ToDictionary(t => t.LogicalName, StringComparer.Ordinal);
        Choices = choices;
    }

    public static Metadata Load()
    {
        using var stream = typeof(Metadata).Assembly.GetManifestResourceStream("model.json")!;
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        var choices = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var c in root.GetProperty("choices").EnumerateObject())
            choices[c.Name] = c.Value.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("value").GetInt32()).ToHashSet();
        var tables = new List<TableMeta>();
        foreach (var t in root.GetProperty("tables").EnumerateArray())
        {
            var columns = t.GetProperty("columns").EnumerateArray().Select(c => new ColumnMeta(
                c.GetProperty("name").GetString()!,
                c.GetProperty("type").GetString()!,
                c.TryGetProperty("maxLength", out var ml) ? ml.GetInt32() : null,
                c.TryGetProperty("choice", out var ch) ? ch.GetString() : null,
                c.TryGetProperty("owner", out var ow) ? ow.GetString()! : "crm")).ToDictionary(c => c.Name, StringComparer.Ordinal);
            var keys = t.GetProperty("keys").EnumerateArray()
                .Select(k => new KeyMeta(k.GetProperty("name").GetString()!, k.GetProperty("attributes").EnumerateArray().Select(a => a.GetString()!).ToArray()))
                .ToList();
            tables.Add(new TableMeta(t.GetProperty("logicalName").GetString()!, t.GetProperty("entitySet").GetString()!,
                t.GetProperty("primaryId").GetString()!, t.GetProperty("primaryName").GetString()!, columns, keys));
        }
        return new Metadata(tables, choices);
    }
}
