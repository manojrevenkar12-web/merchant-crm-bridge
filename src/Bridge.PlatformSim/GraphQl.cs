using Bridge.Core.Contracts;

namespace Bridge.PlatformSim;

/// <summary>The platform's merchant API: what the reconciler asks.</summary>
public sealed class Query
{
    public MerchantView? Merchant(string id, [Service] Simulator simulator) =>
        simulator.Snapshot(id) is { } s ? MerchantView.From(s, simulator) : null;

    public IReadOnlyList<MerchantView> Merchants(IReadOnlyList<string> ids, [Service] Simulator simulator) =>
        ids.Select(simulator.Snapshot).OfType<MerchantSnapshot>().Select(s => MerchantView.From(s, simulator)).ToList();
}

public sealed record KycView(string Decision, string? ReasonCode, long Version);

public sealed record StatusView(string Status, string? Reason, long Version);

public sealed record RiskView(decimal ChargebackRatio, string RiskBand, long Version);

public sealed record MerchantView(string MerchantId, string? PlatformAccountId, KycView? Kyc, StatusView? Status, RiskView? Risk, IReadOnlyList<string> Terminals)
{
    public static MerchantView From(MerchantSnapshot s, Simulator sim) => new(
        s.MerchantId,
        s.PlatformAccountId,
        s.Kyc is { } k ? new KycView(k.Decision, k.ReasonCode, k.Version) : null,
        s.Status is { } st ? new StatusView(st.Status, st.Reason, st.Version) : null,
        s.Risk is { } r ? new RiskView(r.ChargebackRatio, r.RiskBand, r.Version) : null,
        sim.Merchants.TryGetValue(s.MerchantId, out var m) ? m.Terminals.ToList() : []);
}
