using System.Text.Json.Nodes;
using Bridge.Core.Contracts;

namespace Bridge.Tests;

/// <summary>Builds platform CloudEvents for tests.</summary>
public static class Events
{
    public static CloudEvent Platform(string type, string merchantId, long sequence, JsonObject data, int version = 1, string? id = null)
    {
        data["merchantId"] = merchantId;
        return new CloudEvent
        {
            Id = id ?? Guid.NewGuid().ToString(),
            Source = "payments-platform",
            Type = type,
            Subject = merchantId,
            Time = DateTimeOffset.Parse("2026-10-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            Sequence = sequence,
            SchemaVersion = version,
            Data = data,
        };
    }

    public static CloudEvent Kyc(string m, long seq, string decision) => Platform(EventTypes.KycDecided, m, seq, new JsonObject { ["decision"] = decision });
    public static CloudEvent Boarded(string m, long seq, string account = "PA-1") => Platform(EventTypes.Boarded, m, seq, new JsonObject { ["platformAccountId"] = account });
    public static CloudEvent Status(string m, long seq, string status) => Platform(EventTypes.StatusChanged, m, seq, new JsonObject { ["status"] = status });
    public static CloudEvent Risk(string m, long seq, string band, decimal ratio) => Platform(EventTypes.RiskChanged, m, seq, new JsonObject { ["riskBand"] = band, ["chargebackRatio"] = ratio });
    public static CloudEvent Terminal(string m, long seq, string terminal) => Platform(EventTypes.TerminalActivated, m, seq, new JsonObject { ["terminalId"] = terminal, ["model"] = "A920", ["activatedAt"] = "2026-10-01T09:00:00Z" });

    public static CloudEvent VolumeV1(string m, long seq, string month, long cents) =>
        Platform(EventTypes.VolumeReported, m, seq, new JsonObject { ["month"] = month, ["volumeCents"] = cents, ["transactions"] = 10 });

    public static CloudEvent VolumeV2(string m, long seq, string month, long gross, long refunds = 0, string currency = "EUR") =>
        Platform(EventTypes.VolumeReported, m, seq, new JsonObject
        {
            ["month"] = month,
            ["gross"] = new JsonObject { ["amountMinor"] = gross, ["currency"] = currency },
            ["transactions"] = 10,
            ["refundsMinor"] = refunds,
        }, version: 2);
}
