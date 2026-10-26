namespace Bridge.Core.Contracts;

public static class EventTypes
{
    public const string KycDecided = "platform.merchant.kyc_decided";
    public const string Boarded = "platform.merchant.boarded";
    public const string TerminalActivated = "platform.merchant.terminal_activated";
    public const string VolumeReported = "platform.merchant.volume_reported";
    public const string RiskChanged = "platform.merchant.risk_changed";
    public const string StatusChanged = "platform.merchant.status_changed";
    public const string Snapshot = "platform.merchant.snapshot";

    public const string OnboardingRequested = "crm.merchant.onboarding_requested";
    public const string DetailsChanged = "crm.merchant.details_changed";

    /// <summary>Schema versions this bridge reads, per type (it writes the newest).</summary>
    public static readonly IReadOnlyDictionary<string, int[]> Readable = new Dictionary<string, int[]>
    {
        [KycDecided] = [1],
        [Boarded] = [1],
        [TerminalActivated] = [1],
        [VolumeReported] = [1, 2],
        [RiskChanged] = [1],
        [StatusChanged] = [1],
        [Snapshot] = [1],
        [OnboardingRequested] = [1],
        [DetailsChanged] = [1],
    };
}

public static class Topics
{
    public const string PlatformEvents = "platform.merchant.events";
    public const string CrmEvents = "crm.merchant.events";
    public const string PlatformEventsDlq = "platform.merchant.events.dlq";
}
