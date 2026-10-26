using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bridge.Core.Contracts;

/// <summary>What the platform tells the CRM. Records are the newest schema version; older versions are
/// upcast when read (<see cref="EventReader"/>).</summary>
public abstract record PlatformEvent(string MerchantId);

public sealed record KycDecided(string MerchantId, string Decision, string? ReasonCode) : PlatformEvent(MerchantId);

public sealed record Boarded(string MerchantId, string PlatformAccountId) : PlatformEvent(MerchantId);

public sealed record TerminalActivated(string MerchantId, string TerminalId, string? Model, DateTimeOffset ActivatedAt) : PlatformEvent(MerchantId);

public sealed record Money(long AmountMinor, string Currency);

public sealed record VolumeReported(string MerchantId, string Month, Money Gross, long Transactions, long RefundsMinor) : PlatformEvent(MerchantId);

public sealed record RiskChanged(string MerchantId, decimal ChargebackRatio, string RiskBand) : PlatformEvent(MerchantId);

public sealed record StatusChanged(string MerchantId, string Status, string? Reason) : PlatformEvent(MerchantId);

public sealed record SnapshotKyc(string Decision, string? ReasonCode, long Version);

public sealed record SnapshotStatus(string Status, string? Reason, long Version);

public sealed record SnapshotRisk(decimal ChargebackRatio, string RiskBand, long Version);

/// <summary>The platform's whole current view of a merchant, with the sequence of the last change to
/// each aspect. Used by the reconciler.</summary>
public sealed record MerchantSnapshot(string MerchantId, string? PlatformAccountId, SnapshotKyc? Kyc, SnapshotStatus? Status, SnapshotRisk? Risk)
    : PlatformEvent(MerchantId);

/// <summary>Turns a CloudEvent into a typed platform event: known type, readable version, valid
/// against its schema, subject equal to the merchant in the data, older versions upcast.</summary>
public sealed class EventReader(SchemaRegistry schemas)
{
    public static EventReader Default { get; } = new(SchemaRegistry.Default);

    public PlatformEvent Read(CloudEvent ce)
    {
        if (!EventTypes.Readable.TryGetValue(ce.Type, out var versions) || !ce.Type.StartsWith("platform.", StringComparison.Ordinal))
            throw new ContractException($"unknown event type {ce.Type}");
        if (!versions.Contains(ce.SchemaVersion))
            throw new ContractException($"{ce.Type} v{ce.SchemaVersion} is not readable (reads {string.Join(", ", versions)})");
        if (ce.Sequence <= 0) throw new ContractException("sequence must be positive");

        schemas.Validate(ce.Type, ce.SchemaVersion, ce.Data);
        // Re-read from text so numbers behave the same whether the node was parsed or built in code.
        var data = JsonNode.Parse(ce.Data.ToJsonString())!.AsObject();
        var merchantId = (string)data["merchantId"]!;
        if (merchantId != ce.Subject) throw new ContractException($"subject {ce.Subject} differs from data.merchantId {merchantId}");

        return ce.Type switch
        {
            EventTypes.KycDecided => new KycDecided(merchantId, Str(data, "decision")!, Str(data, "reasonCode")),
            EventTypes.Boarded => new Boarded(merchantId, Str(data, "platformAccountId")!),
            EventTypes.TerminalActivated => new TerminalActivated(merchantId, Str(data, "terminalId")!, Str(data, "model"),
                DateTimeOffset.Parse(Str(data, "activatedAt")!, System.Globalization.CultureInfo.InvariantCulture)),
            EventTypes.VolumeReported when ce.SchemaVersion == 1 => new VolumeReported(merchantId, Str(data, "month")!,
                new Money(Long(data, "volumeCents"), "EUR"), Long(data, "transactions"), 0),
            EventTypes.VolumeReported => new VolumeReported(merchantId, Str(data, "month")!,
                new Money(Long(data["gross"]!.AsObject(), "amountMinor"), Str(data["gross"]!.AsObject(), "currency")!),
                Long(data, "transactions"), data["refundsMinor"] is null ? 0 : Long(data, "refundsMinor")),
            EventTypes.RiskChanged => new RiskChanged(merchantId, data["chargebackRatio"]!.GetValue<decimal>(), Str(data, "riskBand")!),
            EventTypes.StatusChanged => new StatusChanged(merchantId, Str(data, "status")!, Str(data, "reason")),
            EventTypes.Snapshot => ReadSnapshot(merchantId, data),
            _ => throw new ContractException($"unknown event type {ce.Type}"),
        };
    }

    private static MerchantSnapshot ReadSnapshot(string merchantId, JsonObject data)
    {
        SnapshotKyc? kyc = data["kyc"] is JsonObject k ? new(Str(k, "decision")!, Str(k, "reasonCode"), Long(k, "version")) : null;
        SnapshotStatus? status = data["status"] is JsonObject s ? new(Str(s, "status")!, Str(s, "reason"), Long(s, "version")) : null;
        SnapshotRisk? risk = data["risk"] is JsonObject r ? new(r["chargebackRatio"]!.GetValue<decimal>(), Str(r, "riskBand")!, Long(r, "version")) : null;
        return new MerchantSnapshot(merchantId, Str(data, "platformAccountId"), kyc, status, risk);
    }

    private static string? Str(JsonObject o, string name) =>
        o[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static long Long(JsonObject o, string name) => o[name]!.GetValue<long>();
}
