using Bridge.Core.Domain;

namespace Bridge.Dataverse;

/// <summary>Choice (option set) values from dataverse/model.json, by the names the domain uses. A test
/// reads model.json and checks every value here.</summary>
public static class Choices
{
    public static readonly IReadOnlyDictionary<LifecycleStage, int> Stage = new Dictionary<LifecycleStage, int>
    {
        [LifecycleStage.Prospect] = 771000000,
        [LifecycleStage.Onboarding] = 771000001,
        [LifecycleStage.KycPending] = 771000002,
        [LifecycleStage.Approved] = 771000003,
        [LifecycleStage.Rejected] = 771000004,
        [LifecycleStage.Boarded] = 771000005,
        [LifecycleStage.Live] = 771000006,
        [LifecycleStage.Restricted] = 771000007,
        [LifecycleStage.Terminated] = 771000008,
    };

    public static readonly IReadOnlyDictionary<string, int> LegalForm = Map(("gmbh", 771000100), ("ug", 771000101), ("ag", 771000102), ("ek", 771000103), ("gbr", 771000104), ("sole_trader", 771000105), ("other", 771000106));
    public static readonly IReadOnlyDictionary<string, int> PricingPlan = Map(("starter", 771000200), ("growth", 771000201), ("enterprise", 771000202));
    public static readonly IReadOnlyDictionary<string, int> KycDecision = Map(("approved", 771000300), ("rejected", 771000301), ("more_information", 771000302));
    public static readonly IReadOnlyDictionary<string, int> RiskBand = Map(("low", 771000400), ("medium", 771000401), ("high", 771000402));
    public static readonly IReadOnlyDictionary<string, int> OutboxStatus = Map(("pending", 771000500), ("sent", 771000501));
    public static readonly IReadOnlyDictionary<string, int> InboundOutcome = Map(("applied", 771000600), ("stale", 771000601), ("rejected", 771000602));
    public static readonly IReadOnlyDictionary<string, int> DriftResolution = Map(("healed", 771000700), ("needs_review", 771000701));
    public static readonly IReadOnlyDictionary<string, int> PlatformStatus = Map(("live", 771000800), ("restricted", 771000801), ("terminated", 771000802));

    public static string? Name(IReadOnlyDictionary<string, int> map, int? value) =>
        value is null ? null : map.FirstOrDefault(kv => kv.Value == value).Key;

    public static int? Value(IReadOnlyDictionary<string, int> map, string? name) =>
        name is null ? null : map.TryGetValue(name, out var v) ? v : throw new ArgumentException($"unknown choice '{name}'", nameof(name));

    public static LifecycleStage StageOf(int? value) =>
        value is null ? LifecycleStage.Prospect : Stage.First(kv => kv.Value == value).Key;

    private static Dictionary<string, int> Map(params (string Name, int Value)[] items) =>
        items.ToDictionary(i => i.Name, i => i.Value, StringComparer.Ordinal);
}
