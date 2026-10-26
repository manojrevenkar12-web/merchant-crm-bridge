using System;
using System.Collections.Generic;

namespace MerchantCrmBridge.Plugins.Logic;

/// <summary>Logical names and choice values from dataverse/model.json (a test keeps them in step).</summary>
public static class Model
{
    public const string Account = "account";
    public const string MerchantId = "mcb_merchantid";
    public const string LifecycleStage = "mcb_lifecyclestage";
    public const string CrmVersion = "mcb_crmversion";
    public const string LegalName = "name";
    public const string ContactEmail = "emailaddress1";
    public const string LegalForm = "mcb_legalform";
    public const string Country = "mcb_country";
    public const string Iban = "mcb_iban";
    public const string Mcc = "mcb_mcc";
    public const string PricingPlan = "mcb_pricingplan";
    public const string KycDecision = "mcb_kycdecision";
    public const string KycReason = "mcb_kycreason";

    public static class Stage
    {
        public const int Prospect = 771000000;
        public const int Onboarding = 771000001;
        public const int KycPending = 771000002;
        public const int Approved = 771000003;
        public const int Rejected = 771000004;
        public const int Boarded = 771000005;
        public const int Live = 771000006;
        public const int Restricted = 771000007;
        public const int Terminated = 771000008;
    }

    public static readonly IReadOnlyDictionary<int, string> LegalForms = new Dictionary<int, string>
    {
        [771000100] = "gmbh", [771000101] = "ug", [771000102] = "ag", [771000103] = "ek",
        [771000104] = "gbr", [771000105] = "sole_trader", [771000106] = "other",
    };

    public static readonly IReadOnlyDictionary<int, string> PricingPlans = new Dictionary<int, string>
    {
        [771000200] = "starter", [771000201] = "growth", [771000202] = "enterprise",
    };

    /// <summary>CRM-owned columns that the platform needs, and their name in the event contract.</summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> SyncedCrmColumns = new[]
    {
        new KeyValuePair<string, string>(LegalName, "legalName"),
        new KeyValuePair<string, string>(ContactEmail, "contactEmail"),
        new KeyValuePair<string, string>(LegalForm, "legalForm"),
        new KeyValuePair<string, string>(Country, "country"),
        new KeyValuePair<string, string>(Iban, "iban"),
        new KeyValuePair<string, string>(Mcc, "mcc"),
        new KeyValuePair<string, string>(PricingPlan, "pricingPlan"),
    };

    public static string EventValue(string column, object? value)
    {
        if (value is null) return string.Empty;
        if (column == LegalForm && value is int lf && LegalForms.TryGetValue(lf, out var lfName)) return lfName;
        if (column == PricingPlan && value is int pp && PricingPlans.TryGetValue(pp, out var ppName)) return ppName;
        if (column == Iban) return Rules.NormalizeIban(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
