namespace Bridge.Core.Domain;

/// <summary>A merchant as the CRM holds it, in domain terms (choices by name). The gateway maps to and
/// from Dataverse columns.</summary>
public sealed record CrmMerchant
{
    public required Guid AccountId { get; init; }
    public required string MerchantId { get; init; }

    /// <summary>The row's version for optimistic concurrency (If-Match).</summary>
    public required string ETag { get; init; }

    public LifecycleStage Stage { get; init; } = LifecycleStage.Prospect;
    public long CrmVersion { get; init; }

    // CRM-owned
    public string? LegalName { get; init; }
    public string? ContactEmail { get; init; }
    public string? LegalForm { get; init; }
    public string? Country { get; init; }
    public string? Iban { get; init; }
    public string? Mcc { get; init; }
    public string? PricingPlan { get; init; }

    // Platform-owned, each aspect with the platform sequence it was last set at
    public string? PlatformAccountId { get; init; }
    public string? KycDecision { get; init; }
    public string? KycReason { get; init; }
    public long KycVersion { get; init; }
    public string? PlatformStatus { get; init; }
    public string? StatusReason { get; init; }
    public long StatusVersion { get; init; }
    public decimal? ChargebackRatio { get; init; }
    public string? RiskBand { get; init; }
    public long RiskVersion { get; init; }
    public string? VolumeMonth { get; init; }
    public decimal? LastMonthVolume { get; init; }
    public int? LastMonthTransactions { get; init; }
    public long VolumeVersion { get; init; }
    public long PlatformVersion { get; init; }
    public DateTimeOffset? LastSyncedOn { get; init; }
}

public enum LifecycleStage
{
    Prospect,
    Onboarding,
    KycPending,
    Approved,
    Rejected,
    Boarded,
    Live,
    Restricted,
    Terminated,
}
