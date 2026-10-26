using System.Collections.Generic;

namespace MerchantCrmBridge.Plugins.Logic;

/// <summary>PreOperation on Update of account (synchronous, inside the database transaction): when a
/// column the platform needs changes on a merchant the platform knows, raise the CRM version and write an
/// outbox row. The row commits with the change or not at all, so no change is lost and none is invented.</summary>
public static class MerchantOutboxLogic
{
    public const string EventType = "crm.merchant.details_changed";
    public const int SchemaVersion = 1;

    /// <summary>Returns the outbox entry written, or null when nothing needs to reach the platform.</summary>
    public static OutboxEntry? Execute(IRecord target, IRecord? preImage, PluginContext context, IOutboxSink sink)
    {
        // The bridge writes only platform-owned columns; never echo its own writes back to the platform.
        if (context.IsBridge) return null;

        var row = new MergedRecord(target, preImage);
        var stage = row.GetInt(Model.LifecycleStage) ?? Model.Stage.Prospect;
        if (stage == Model.Stage.Prospect || stage == Model.Stage.Rejected || stage == Model.Stage.Terminated) return null;

        var merchantId = row.GetString(Model.MerchantId);
        if (string.IsNullOrEmpty(merchantId)) return null;

        var changes = new List<KeyValuePair<string, string>>();
        foreach (var column in Model.SyncedCrmColumns)
        {
            if (row.Changed(column.Key))
                changes.Add(new KeyValuePair<string, string>(column.Value, Model.EventValue(column.Key, target.Get(column.Key))));
        }
        if (changes.Count == 0) return null;

        var sequence = row.GetLong(Model.CrmVersion) + 1;
        target.Set(Model.CrmVersion, sequence);
        var entry = new OutboxEntry(
            context.NewId().ToString(),
            EventType,
            merchantId!,
            sequence,
            SchemaVersion,
            Json.WithChanges(merchantId!, changes));
        sink.Write(entry);
        return entry;
    }
}
