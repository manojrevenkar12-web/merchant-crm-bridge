using System;
using MerchantCrmBridge.Plugins.Logic;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace MerchantCrmBridge.Plugins;

/// <summary>account Create/Update, PreValidation: IBAN, MCC, country, merchant id and e-mail are valid
/// before anything else runs, so a sales user sees the problem on save.</summary>
public sealed class ValidateMerchantPlugin : PluginBase
{
    protected override void Execute(IPluginExecutionContext context, IOrganizationServiceFactory factory, ITracingService tracing)
    {
        if (!(context.InputParameters["Target"] is Entity target)) return;
        var pre = PreImage(context);
        ValidateMerchantLogic.Execute(new EntityRecord(target), pre == null ? null : new EntityRecord(pre));
    }
}

/// <summary>account Update, PreOperation (inside the transaction), filtered to the synced columns: raises
/// mcb_crmversion and writes the outbox row, so the change and its event commit or roll back together.
/// Changes the bridge itself makes (the integration user) are not echoed back to the platform.</summary>
public sealed class MerchantOutboxPlugin : PluginBase
{
    protected override void Execute(IPluginExecutionContext context, IOrganizationServiceFactory factory, ITracingService tracing)
    {
        if (!(context.InputParameters["Target"] is Entity target)) return;
        var system = factory.CreateOrganizationService(null);
        var bridge = new PluginContext(context.InitiatingUserId, EnvironmentVariables.IntegrationUserId(system), Guid.NewGuid);
        var pre = PreImage(context);
        var entry = MerchantOutboxLogic.Execute(new EntityRecord(target), pre == null ? null : new EntityRecord(pre), bridge, new OutboxSink(system));
        if (entry != null) tracing.Trace("outbox {0} {1} #{2}", entry.EventType, entry.MerchantId, entry.Sequence);
    }
}

/// <summary>Custom API mcb_RequestOnboarding (bound to account), main operation: checks completeness, moves
/// the merchant to onboarding and writes the onboarding_requested event, all in the API's transaction.</summary>
public sealed class RequestOnboardingPlugin : PluginBase
{
    private static readonly ColumnSet Columns = new ColumnSet(
        Model.MerchantId, Model.LifecycleStage, Model.CrmVersion, Model.LegalName, Model.ContactEmail, Model.LegalForm,
        Model.Country, Model.Iban, Model.Mcc, Model.PricingPlan, Model.KycDecision, Model.KycReason);

    protected override void Execute(IPluginExecutionContext context, IOrganizationServiceFactory factory, ITracingService tracing)
    {
        var target = (EntityReference)context.InputParameters["Target"];
        var user = factory.CreateOrganizationService(context.UserId);
        var system = factory.CreateOrganizationService(null);
        var current = user.Retrieve(target.LogicalName, target.Id, Columns); // the caller must be able to read the merchant
        var update = new Entity(target.LogicalName, target.Id);
        var bridge = new PluginContext(context.InitiatingUserId, EnvironmentVariables.IntegrationUserId(system), Guid.NewGuid);
        var entry = RequestOnboardingLogic.Execute(new EntityRecord(current), new EntityRecord(update), bridge, new OutboxSink(system));
        system.Update(update); // KYC columns are secured for people; the API clears them on a new request
        context.OutputParameters["EventId"] = entry.EventId;
    }
}
