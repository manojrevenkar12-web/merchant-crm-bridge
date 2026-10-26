using System.Collections.Generic;

namespace MerchantCrmBridge.Plugins.Logic;

/// <summary>PreValidation on Create and Update of account: refuse bad settlement data before the
/// transaction starts, and keep the merchant id fixed once the platform knows it.</summary>
public static class ValidateMerchantLogic
{
    public static void Execute(IRecord target, IRecord? preImage)
    {
        var problems = new List<string>();
        void Check(string column, System.Func<string?, string?> rule)
        {
            if (!target.Contains(column)) return;
            var value = target.Get(column) as string;
            if (string.IsNullOrEmpty(value)) return; // clearing a value is allowed for prospects; onboarding checks completeness
            var problem = rule(value);
            if (problem != null) problems.Add(problem);
        }

        Check(Model.Iban, Rules.CheckIban);
        Check(Model.Mcc, Rules.CheckMcc);
        Check(Model.Country, Rules.CheckCountry);
        Check(Model.MerchantId, Rules.CheckMerchantId);

        if (preImage != null && target.Contains(Model.MerchantId))
        {
            var stage = preImage.Get(Model.LifecycleStage) as int?;
            var before = preImage.Get(Model.MerchantId) as string;
            var after = target.Get(Model.MerchantId) as string;
            if (stage.HasValue && stage.Value != Model.Stage.Prospect && before != after)
                problems.Add("Merchant ID cannot change after onboarding was requested.");
        }

        if (problems.Count > 0) throw new PluginRuleException(string.Join(" ", problems));
    }
}
