using System.Text.Json.Nodes;
using Bridge.Core.Contracts;

namespace Bridge.Tests;

/// <summary>The event contracts: schemas load, readers refuse what breaks them, and every schema version
/// the bridge reads stays readable.</summary>
public class ContractTests
{
    [Fact]
    public void Every_readable_type_and_version_has_a_schema()
    {
        foreach (var (type, versions) in EventTypes.Readable)
            foreach (var v in versions)
                Assert.True(SchemaRegistry.Default.Has(type, v), $"{type} v{v}");
    }

    [Theory]
    [InlineData("{}", "missing required properties")]
    [InlineData("{\"specversion\":\"1.0\",\"id\":\"\",\"source\":\"s\",\"type\":\"t\",\"subject\":\"M-000001\",\"time\":\"2026-10-01T00:00:00Z\",\"data\":{}}", "id, type and subject are required")]
    [InlineData("not json", "not a CloudEvent")]
    [InlineData("{\"specversion\":\"0.3\",\"id\":\"1\",\"source\":\"s\",\"type\":\"t\",\"subject\":\"M-000001\",\"time\":\"2026-10-01T00:00:00Z\",\"data\":{}}", "specversion")]
    public void Malformed_envelopes_are_contract_errors(string json, string message)
    {
        var ex = Assert.Throws<ContractException>(() => CloudEvent.Parse(json));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_types_unreadable_versions_and_invalid_data_are_refused()
    {
        var reader = EventReader.Default;
        Assert.Throws<ContractException>(() => reader.Read(Events.Platform("platform.merchant.renamed", "M-000001", 1, [])));
        Assert.Throws<ContractException>(() => reader.Read(Events.Platform(EventTypes.KycDecided, "M-000001", 1, new JsonObject { ["decision"] = "approved" }, version: 3)));
        var bad = Assert.Throws<ContractException>(() => reader.Read(Events.Platform(EventTypes.KycDecided, "M-000001", 1, new JsonObject { ["decision"] = "maybe" })));
        Assert.Contains("does not match its schema", bad.Message, StringComparison.Ordinal);
        Assert.Throws<ContractException>(() => reader.Read(Events.Risk("M-000001", 1, "high", 1.5m)));
        Assert.Throws<ContractException>(() => reader.Read(Events.Kyc("M-000001", 0, "approved")));
    }

    [Fact]
    public void The_subject_must_be_the_merchant_in_the_data()
    {
        var ce = Events.Kyc("M-000001", 1, "approved") with { Subject = "M-000002" };
        Assert.Contains("differs", Assert.Throws<ContractException>(() => EventReader.Default.Read(ce)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cloud_event_round_trips_through_json()
    {
        var ce = Events.VolumeV2("M-000001", 4, "2026-09", 100);
        var back = CloudEvent.Parse(ce.Serialize());
        Assert.Equal(ce.Id, back.Id);
        Assert.Equal(4, back.Sequence);
        Assert.Equal(2, back.SchemaVersion);
        Assert.Equal(100, (long)back.Data["gross"]!["amountMinor"]!);
    }

    [Fact]
    public void Examples_in_the_repository_are_valid()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "examples");
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories) : [];
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var ce = CloudEvent.Parse(File.ReadAllText(file));
            SchemaRegistry.Default.Validate(ce.Type, ce.SchemaVersion, ce.Data);
        }
    }
}
