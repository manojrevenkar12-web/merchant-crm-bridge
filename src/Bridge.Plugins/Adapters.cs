using System;
using System.Linq;
using MerchantCrmBridge.Plugins.Logic;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace MerchantCrmBridge.Plugins;

/// <summary>An Entity as the rules see it: choices as int, money as decimal, lookups as Guid. The rules only
/// ever set choices (int) and versions (long), so an int written back becomes an OptionSetValue.</summary>
internal sealed class EntityRecord : IRecord
{
    public EntityRecord(Entity entity) => Entity = entity;

    public Entity Entity { get; }

    public bool Contains(string column) => Entity.Attributes.ContainsKey(column);

    public object? Get(string column)
    {
        if (!Entity.Attributes.TryGetValue(column, out var value)) return null;
        switch (value)
        {
            case OptionSetValue o: return o.Value;
            case Money m: return m.Value;
            case EntityReference r: return r.Id;
            default: return value;
        }
    }

    public void Set(string column, object? value) => Entity[column] = value is int i ? new OptionSetValue(i) : value;
}

/// <summary>Outbox rows are created with the system service: people cannot write the outbox table.</summary>
internal sealed class OutboxSink : IOutboxSink
{
    private readonly IOrganizationService _system;

    public OutboxSink(IOrganizationService system) => _system = system;

    public void Write(OutboxEntry entry)
    {
        var row = new Entity("mcb_outboxmessage")
        {
            ["mcb_name"] = entry.EventType + " " + entry.MerchantId + " #" + entry.Sequence,
            ["mcb_eventid"] = entry.EventId,
            ["mcb_eventtype"] = entry.EventType,
            ["mcb_merchantid"] = entry.MerchantId,
            ["mcb_sequence"] = entry.Sequence,
            ["mcb_schemaversion"] = entry.SchemaVersion,
            ["mcb_payload"] = entry.PayloadJson,
            ["mcb_status"] = new OptionSetValue(771000500),
            ["mcb_attempts"] = 0,
        };
        _system.Create(row);
    }
}

internal static class EnvironmentVariables
{
    /// <summary>The current value of an environment variable (its value row, else its default).</summary>
    public static string? Get(IOrganizationService service, string schemaName)
    {
        var query = new QueryExpression("environmentvariabledefinition")
        {
            ColumnSet = new ColumnSet("defaultvalue"),
            Criteria = { Conditions = { new ConditionExpression("schemaname", ConditionOperator.Equal, schemaName) } },
            TopCount = 1,
        };
        var values = query.AddLink("environmentvariablevalue", "environmentvariabledefinitionid", "environmentvariabledefinitionid", JoinOperator.LeftOuter);
        values.Columns = new ColumnSet("value");
        values.EntityAlias = "v";
        var definition = service.RetrieveMultiple(query).Entities.FirstOrDefault();
        if (definition == null) return null;
        var current = definition.GetAttributeValue<AliasedValue>("v.value")?.Value as string;
        return !string.IsNullOrEmpty(current) ? current : definition.GetAttributeValue<string>("defaultvalue");
    }

    public static Guid? IntegrationUserId(IOrganizationService service) =>
        Guid.TryParse(Get(service, "mcb_IntegrationUserId"), out var id) ? id : (Guid?)null;
}

/// <summary>Common plumbing: services, tracing, and turning a rule into the message the user sees.</summary>
public abstract class PluginBase : IPlugin
{
    public void Execute(IServiceProvider serviceProvider)
    {
        var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
        var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
        var tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
        try
        {
            Execute(context, factory, tracing);
        }
        catch (PluginRuleException ex)
        {
            tracing.Trace("rule: {0}", ex.Message);
            throw new InvalidPluginExecutionException(OperationStatus.Failed, ex.Message);
        }
    }

    protected abstract void Execute(IPluginExecutionContext context, IOrganizationServiceFactory factory, ITracingService tracing);

    protected static Entity? PreImage(IPluginExecutionContext context) =>
        context.PreEntityImages.TryGetValue("pre", out var image) ? image : null;
}
