using System.Collections.Generic;

namespace MerchantCrmBridge.Plugins.Logic;

/// <summary>Custom API mcb_RequestOnboarding (bound to account): sales hands a won merchant to the
/// platform. Everything the platform needs must be present and valid; then the stage moves to
/// onboarding and the full details go out through the outbox in the same transaction.</summary>
public static class RequestOnboardingLogic
{
    public const string EventType = "crm.merchant.onboarding_requested";
    public const int SchemaVersion = 1;

    /// <param name="current">The account as stored.</param>
    /// <param name="update">Receives the columns to update on the account.</param>
    public static OutboxEntry Execute(IRecord current, IRecord update, PluginContext context, IOutboxSink sink)
    {
        var stage = current.Get(Model.LifecycleStage) as int? ?? Model.Stage.Prospect;
        if (stage != Model.Stage.Prospect && stage != Model.Stage.Rejected)
            throw new PluginRuleException("Onboarding can only be requested for a prospect or a rejected merchant.");

        var problems = new List<string>();
        void Need(string? problem) { if (problem != null) problems.Add(problem); }
        Need(Rules.CheckMerchantId(current.Get(Model.MerchantId) as string));
        Need(string.IsNullOrWhiteSpace(current.Get(Model.LegalName) as string) ? "Legal name is missing." : null);
        Need(current.Get(Model.LegalForm) is int ? null : "Legal form is missing.");
        Need(Rules.CheckCountry(current.Get(Model.Country) as string));
        Need(Rules.CheckIban(current.Get(Model.Iban) as string));
        Need(Rules.CheckMcc(current.Get(Model.Mcc) as string));
        Need(current.Get(Model.PricingPlan) is int ? null : "Pricing plan is missing.");
        Need(Rules.CheckEmail(current.Get(Model.ContactEmail) as string));
        if (problems.Count > 0) throw new PluginRuleException("Onboarding cannot be requested: " + string.Join(" ", problems));

        var merchantId = (string)current.Get(Model.MerchantId)!;
        long version = current.Get(Model.CrmVersion) is long l ? l : 0;
        var sequence = version + 1;
        update.Set(Model.LifecycleStage, Model.Stage.Onboarding);
        update.Set(Model.CrmVersion, sequence);
        if (stage == Model.Stage.Rejected)
        {
            // A new request starts a new KYC: the old decision must not keep deriving "rejected". The
            // adapter writes this with the system service, since the KYC columns are secured for people.
            update.Set(Model.KycDecision, null);
            update.Set(Model.KycReason, null);
        }

        var fields = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("merchantId", merchantId) };
        foreach (var column in Model.SyncedCrmColumns)
            fields.Add(new KeyValuePair<string, string>(column.Value, Model.EventValue(column.Key, current.Get(column.Key))));

        var entry = new OutboxEntry(context.NewId().ToString(), EventType, merchantId, sequence, SchemaVersion, Json.Flat(fields));
        sink.Write(entry);
        return entry;
    }
}
