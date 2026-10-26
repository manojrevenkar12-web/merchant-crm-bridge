namespace Bridge.Core.Domain;

/// <summary>Columns the bridge may write. Everything else in the CRM belongs to people (see
/// config/ownership in docs/adr/0002); a test checks this list against dataverse/model.json.</summary>
public enum PlatformField
{
    Stage,
    PlatformAccountId,
    KycDecision,
    KycReason,
    KycVersion,
    PlatformStatus,
    StatusReason,
    StatusVersion,
    ChargebackRatio,
    RiskBand,
    RiskVersion,
    VolumeMonth,
    LastMonthVolume,
    LastMonthTransactions,
    VolumeVersion,
    PlatformVersion,
    LastSyncedOn,
}

public sealed class MerchantPatch
{
    private readonly Dictionary<PlatformField, object?> _values = [];

    public IReadOnlyDictionary<PlatformField, object?> Values => _values;

    public bool IsEmpty => _values.Count == 0;

    public MerchantPatch Set(PlatformField field, object? value)
    {
        _values[field] = value;
        return this;
    }

    public bool Has(PlatformField field) => _values.ContainsKey(field);
}

public sealed record TerminalUpsert(string TerminalId, string MerchantId, string? Model, DateTimeOffset ActivatedAt);
