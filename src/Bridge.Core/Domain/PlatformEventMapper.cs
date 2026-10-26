using Bridge.Core.Contracts;

namespace Bridge.Core.Domain;

public enum Outcome
{
    Applied,
    Stale,
    Rejected,
    Duplicate,
}

/// <summary>What applying one platform event to the CRM means.</summary>
public sealed record Effect(Outcome Outcome, string Detail, MerchantPatch Patch, TerminalUpsert? Terminal)
{
    public bool WritesMerchant => !Patch.IsEmpty;
}

/// <summary>
/// Decides how a platform event changes the CRM. Events can arrive twice, late or out of order, so
/// each aspect of the merchant (KYC, status, risk, volume) carries the platform sequence it was last
/// set at and only a newer sequence replaces it; facts with their own key (a terminal, a platform
/// account) are applied idempotently whatever their order. The lifecycle stage is derived from those
/// aspects, never moved step by step, so any order of the same events ends in the same row.
/// </summary>
public static class PlatformEventMapper
{
    public static Effect Decide(CrmMerchant m, PlatformEvent e, long sequence, DateTimeOffset now)
    {
        if (m.Stage == LifecycleStage.Prospect)
            return Reject("the CRM never handed this merchant to the platform (stage prospect)");

        var patch = new MerchantPatch();
        TerminalUpsert? terminal = null;
        var after = m;
        var changed = new List<string>();

        switch (e)
        {
            case KycDecided k:
                if (sequence > m.KycVersion)
                {
                    after = after with { KycDecision = k.Decision, KycReason = k.ReasonCode, KycVersion = sequence };
                    patch.Set(PlatformField.KycDecision, k.Decision).Set(PlatformField.KycReason, k.ReasonCode).Set(PlatformField.KycVersion, sequence);
                    changed.Add("kyc");
                }
                break;

            case Boarded b:
                if (m.PlatformAccountId is null)
                {
                    after = after with { PlatformAccountId = b.PlatformAccountId };
                    patch.Set(PlatformField.PlatformAccountId, b.PlatformAccountId);
                    changed.Add("platform account");
                }
                else if (m.PlatformAccountId != b.PlatformAccountId)
                {
                    return Reject($"platform account {b.PlatformAccountId} conflicts with {m.PlatformAccountId} already in the CRM");
                }
                break;

            case TerminalActivated t:
                terminal = new TerminalUpsert(t.TerminalId, t.MerchantId, t.Model, t.ActivatedAt);
                changed.Add("terminal " + t.TerminalId);
                break;

            case VolumeReported v:
                if (v.Gross.Currency != "EUR")
                    return Reject($"volume in {v.Gross.Currency}; the CRM reports volume in EUR");
                // A month replaces an earlier month; a restatement of the same month needs a newer sequence.
                var newer = string.CompareOrdinal(v.Month, m.VolumeMonth ?? "") > 0
                    || (v.Month == m.VolumeMonth && sequence > m.VolumeVersion);
                if (newer)
                {
                    var amount = (v.Gross.AmountMinor - v.RefundsMinor) / 100m;
                    after = after with { VolumeMonth = v.Month, LastMonthVolume = amount, LastMonthTransactions = (int)Math.Min(v.Transactions, int.MaxValue), VolumeVersion = sequence };
                    patch.Set(PlatformField.VolumeMonth, v.Month)
                        .Set(PlatformField.LastMonthVolume, amount)
                        .Set(PlatformField.LastMonthTransactions, after.LastMonthTransactions)
                        .Set(PlatformField.VolumeVersion, sequence);
                    changed.Add("volume " + v.Month);
                }
                break;

            case RiskChanged r:
                if (sequence > m.RiskVersion)
                {
                    after = after with { ChargebackRatio = r.ChargebackRatio, RiskBand = r.RiskBand, RiskVersion = sequence };
                    patch.Set(PlatformField.ChargebackRatio, r.ChargebackRatio).Set(PlatformField.RiskBand, r.RiskBand).Set(PlatformField.RiskVersion, sequence);
                    changed.Add("risk");
                }
                break;

            case StatusChanged s:
                if (sequence > m.StatusVersion)
                {
                    after = after with { PlatformStatus = s.Status, StatusReason = s.Reason, StatusVersion = sequence };
                    patch.Set(PlatformField.PlatformStatus, s.Status).Set(PlatformField.StatusReason, s.Reason).Set(PlatformField.StatusVersion, sequence);
                    changed.Add("status");
                }
                break;

            case MerchantSnapshot snap:
                if (snap.PlatformAccountId is not null && m.PlatformAccountId is null)
                {
                    after = after with { PlatformAccountId = snap.PlatformAccountId };
                    patch.Set(PlatformField.PlatformAccountId, snap.PlatformAccountId);
                    changed.Add("platform account");
                }
                if (snap.Kyc is { } sk && sk.Version > m.KycVersion)
                {
                    after = after with { KycDecision = sk.Decision, KycReason = sk.ReasonCode, KycVersion = sk.Version };
                    patch.Set(PlatformField.KycDecision, sk.Decision).Set(PlatformField.KycReason, sk.ReasonCode).Set(PlatformField.KycVersion, sk.Version);
                    changed.Add("kyc");
                }
                if (snap.Status is { } ss && ss.Version > m.StatusVersion)
                {
                    after = after with { PlatformStatus = ss.Status, StatusReason = ss.Reason, StatusVersion = ss.Version };
                    patch.Set(PlatformField.PlatformStatus, ss.Status).Set(PlatformField.StatusReason, ss.Reason).Set(PlatformField.StatusVersion, ss.Version);
                    changed.Add("status");
                }
                if (snap.Risk is { } sr && sr.Version > m.RiskVersion)
                {
                    after = after with { ChargebackRatio = sr.ChargebackRatio, RiskBand = sr.RiskBand, RiskVersion = sr.Version };
                    patch.Set(PlatformField.ChargebackRatio, sr.ChargebackRatio).Set(PlatformField.RiskBand, sr.RiskBand).Set(PlatformField.RiskVersion, sr.Version);
                    changed.Add("risk");
                }
                break;

            default:
                return Reject("unhandled event " + e.GetType().Name);
        }

        if (changed.Count == 0)
            return new Effect(Outcome.Stale, "a newer state is already in the CRM", new MerchantPatch(), null);

        if (!patch.IsEmpty)
        {
            var stage = DeriveStage(after);
            if (stage != m.Stage) patch.Set(PlatformField.Stage, stage);
            var highest = Math.Max(m.PlatformVersion, sequence);
            if (highest != m.PlatformVersion) patch.Set(PlatformField.PlatformVersion, highest);
            patch.Set(PlatformField.LastSyncedOn, now);
        }
        return new Effect(Outcome.Applied, string.Join(", ", changed), patch, terminal);
    }

    /// <summary>The stage follows from what the platform said, most significant first. Before the
    /// platform has said anything the CRM's own stage (prospect, onboarding) stands.</summary>
    public static LifecycleStage DeriveStage(CrmMerchant m) => m.PlatformStatus switch
    {
        "terminated" => LifecycleStage.Terminated,
        "restricted" => LifecycleStage.Restricted,
        "live" => LifecycleStage.Live,
        _ when m.PlatformAccountId is not null => LifecycleStage.Boarded,
        _ => m.KycDecision switch
        {
            "approved" => LifecycleStage.Approved,
            "rejected" => LifecycleStage.Rejected,
            "more_information" => LifecycleStage.KycPending,
            _ => m.Stage,
        },
    };

    private static Effect Reject(string why) => new(Outcome.Rejected, why, new MerchantPatch(), null);
}
