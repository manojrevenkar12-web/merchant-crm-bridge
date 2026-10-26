using System.Text.Json;
using Bridge.Core.Domain;
using Bridge.Dataverse;
using MerchantCrmBridge.Plugins.Logic;

namespace Bridge.Tests;

/// <summary>dataverse/model.json is the source of truth; code that names columns and choices must agree.</summary>
public class ModelConsistencyTests
{
    private static readonly JsonElement Root = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "model.json"))).RootElement;

    private static Dictionary<string, int> Options(string choice) =>
        Root.GetProperty("choices").GetProperty(choice).GetProperty("options").EnumerateArray()
            .ToDictionary(o => o.GetProperty("name").GetString()!, o => o.GetProperty("value").GetInt32());

    private static Dictionary<string, JsonElement> AccountColumns =>
        Root.GetProperty("tables").EnumerateArray().First(t => t.GetProperty("logicalName").GetString() == "account")
            .GetProperty("columns").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!, c => c);

    [Fact]
    public void Choice_values_in_code_match_the_model()
    {
        Assert.Equal(Options("mcb_legalform"), Choices.LegalForm);
        Assert.Equal(Options("mcb_pricingplan"), Choices.PricingPlan);
        Assert.Equal(Options("mcb_kycdecision"), Choices.KycDecision);
        Assert.Equal(Options("mcb_riskband"), Choices.RiskBand);
        Assert.Equal(Options("mcb_outboxstatus"), Choices.OutboxStatus);
        Assert.Equal(Options("mcb_inboundoutcome"), Choices.InboundOutcome);
        Assert.Equal(Options("mcb_driftresolution"), Choices.DriftResolution);
        Assert.Equal(Options("mcb_platformstatus"), Choices.PlatformStatus);
        var stages = Options("mcb_lifecyclestage");
        Assert.Equal(stages["prospect"], Choices.Stage[LifecycleStage.Prospect]);
        Assert.Equal(stages["kyc_pending"], Choices.Stage[LifecycleStage.KycPending]);
        Assert.Equal(stages.Count, Choices.Stage.Count);
        Assert.Equal(Model.Stage.Terminated, stages["terminated"]);
        Assert.Equal(Options("mcb_legalform").ToDictionary(kv => kv.Value, kv => kv.Key), Model.LegalForms);
    }

    [Fact]
    public void The_bridge_writes_only_platform_owned_columns_and_the_stage()
    {
        var body = DataverseCrmGateway.PatchBody(Enum.GetValues<PlatformField>().Aggregate(new MerchantPatch(), (p, f) => p.Set(f, Sample(f))));
        foreach (var (column, _) in body)
        {
            var owner = AccountColumns[column].GetProperty("owner").GetString();
            Assert.True(owner is "platform" || column == "mcb_lifecyclestage", $"{column} is owned by {owner}");
        }
        // and every platform-owned column has a field
        var platformColumns = AccountColumns.Where(c => c.Value.GetProperty("owner").GetString() == "platform").Select(c => c.Key).ToHashSet();
        Assert.Equal(platformColumns, body.Select(kv => kv.Key).Where(k => k != "mcb_lifecyclestage").ToHashSet());
    }

    [Fact]
    public void The_plug_in_sends_exactly_the_crm_owned_columns_the_platform_needs()
    {
        var withEvent = AccountColumns.Where(c => c.Value.TryGetProperty("event", out _)).ToDictionary(c => c.Key, c => c.Value.GetProperty("event").GetString());
        Assert.Equal(withEvent, Model.SyncedCrmColumns.ToDictionary(kv => kv.Key, kv => (string?)kv.Value));
        Assert.All(withEvent.Keys, c => Assert.Equal("crm", AccountColumns[c].GetProperty("owner").GetString()));
    }

    [Fact]
    public void Outbox_plug_in_filtering_attributes_are_the_synced_columns()
    {
        var step = Root.GetProperty("pluginSteps").EnumerateArray().First(s => s.GetProperty("plugin").GetString()!.EndsWith("MerchantOutboxPlugin", StringComparison.Ordinal));
        var filtering = step.GetProperty("filteringAttributes").EnumerateArray().Select(a => a.GetString()!).ToHashSet();
        Assert.Equal(Model.SyncedCrmColumns.Select(kv => kv.Key).ToHashSet(), filtering);
    }

    private static object? Sample(PlatformField f) => f switch
    {
        PlatformField.Stage => LifecycleStage.Live,
        PlatformField.KycDecision => "approved",
        PlatformField.PlatformStatus => "live",
        PlatformField.RiskBand => "low",
        PlatformField.ChargebackRatio or PlatformField.LastMonthVolume => 1m,
        PlatformField.LastMonthTransactions => 1,
        PlatformField.LastSyncedOn => DateTimeOffset.UtcNow,
        PlatformField.KycVersion or PlatformField.StatusVersion or PlatformField.RiskVersion or PlatformField.VolumeVersion or PlatformField.PlatformVersion => 1L,
        _ => "x",
    };

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "merchant-crm-bridge.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    [Fact]
    public void Every_registered_plugin_type_exists_in_the_plugin_assembly()
    {
        // The plug-in assembly targets .NET Framework and builds in CI; its source is checked here.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bridge.Plugins", "Plugins.cs"));
        var types = Root.GetProperty("pluginSteps").EnumerateArray().Select(s => s.GetProperty("plugin").GetString()!)
            .Concat(Root.GetProperty("customApis").EnumerateArray().Select(a => a.GetProperty("plugin").GetString()!)).Distinct().ToList();
        Assert.NotEmpty(types);
        foreach (var type in types)
        {
            Assert.StartsWith("MerchantCrmBridge.Plugins.", type, StringComparison.Ordinal);
            Assert.Contains($"public sealed class {type["MerchantCrmBridge.Plugins.".Length..]} : PluginBase", source, StringComparison.Ordinal);
        }
    }
}
