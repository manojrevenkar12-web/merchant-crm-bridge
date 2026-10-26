using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MerchantCrmBridge.Plugins.Logic;

namespace Bridge.Emulator;

public sealed record Caller(Guid UserId, string Name, bool IsIntegration, bool IsSystem = false)
{
    public static readonly Caller System = new(Guid.Parse("00000000-0000-0000-0000-00000000000a"), "SYSTEM", false, true);
}

public sealed class EmulatorOptions
{
    public Guid IntegrationUserId { get; set; } = Guid.Parse("9e0f2c1d-6b8a-4c7e-9f31-6d2b7c4a5e10");
    public string IntegrationClientId { get; set; } = "merchant-crm-bridge";
    public string IntegrationClientSecret { get; set; } = "local-development-secret";

    /// <summary>Service protection: requests per caller in the window (Dataverse default 6000 in 300 s).</summary>
    public int ThrottleLimit { get; set; } = 6000;
    public TimeSpan ThrottleWindow { get; set; } = TimeSpan.FromSeconds(300);

    public TimeSpan MinLatency { get; set; } = TimeSpan.Zero;
    public TimeSpan MaxLatency { get; set; } = TimeSpan.Zero;

    /// <summary>Fail this many upcoming $batch requests with 503 (the drills use it).</summary>
    public int FailNextBatches { get; set; }

    public string BaseUrl { get; set; } = "http://localhost/api/data/v9.2/";
}

/// <summary>A reference to a row: by primary id or by alternate key values.</summary>
public sealed record RowRef(Guid? Id, IReadOnlyDictionary<string, object?>? Keys);

/// <summary>Dataverse semantics without HTTP: create, retrieve, update/upsert with concurrency headers,
/// delete, query, the custom API, column security and the plug-in pipeline, all inside a transaction.</summary>
public sealed class Engine(Store store, EmulatorOptions options, TimeProvider clock)
{
    public Store Store => store;
    public EmulatorOptions Options => options;

    private TableMeta Table(string set) =>
        store.Metadata.BySet.TryGetValue(set, out var t) ? t : throw new DataverseException(404, "0x80060888", $"Resource not found for the segment '{set}'.");

    // --- value conversion -------------------------------------------------------------------------

    public Dictionary<string, object?> ReadBody(TableMeta table, JsonObject body)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, node) in body)
        {
            if (name.StartsWith('@') || name.Contains('@', StringComparison.Ordinal)) continue; // annotations
            if (!table.Columns.TryGetValue(name, out var col))
            {
                if (name == table.PrimaryId) { values[name] = node is null ? null : Guid.Parse(node.GetValue<string>()); continue; }
                throw new DataverseException(400, "0x80060888", $"Could not find a property named '{name}' on type 'Microsoft.Dynamics.CRM.{table.LogicalName}'.");
            }
            values[name] = Convert(col, node);
        }
        return values;
    }

    private object? Convert(ColumnMeta col, JsonNode? node)
    {
        if (node is null) return null;
        try
        {
            switch (col.Type)
            {
                case "string":
                case "memo":
                    var s = node.GetValue<string>();
                    if (col.MaxLength is { } max && s.Length > max)
                        throw DataverseException.Invalid($"A validation error occurred. The length of the '{col.Name}' attribute exceeded the maximum allowed length of '{max}'.");
                    return s;
                case "choice":
                    var v = node.GetValue<int>();
                    if (col.Choice is not null && !store.Metadata.Choices[col.Choice].Contains(v))
                        throw DataverseException.Invalid($"A validation error occurred. The value {v} of '{col.Name}' is outside the valid range.");
                    return v;
                case "int": return node.GetValue<int>();
                case "bigint": return node.GetValue<long>();
                case "decimal": return node.GetValue<decimal>();
                case "bool": return node.GetValue<bool>();
                case "datetime": return DateTimeOffset.Parse(node.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                default: throw DataverseException.Invalid($"Unsupported column type {col.Type}.");
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or JsonException)
        {
            throw DataverseException.Invalid($"Cannot convert the value of '{col.Name}' to {col.Type}.");
        }
    }

    public JsonObject ToJson(TableMeta table, Row row, IReadOnlyCollection<string>? select)
    {
        var o = new JsonObject { ["@odata.etag"] = ETag(row) };
        foreach (var (k, v) in row.Values)
        {
            if (select is { Count: > 0 } && !select.Contains(k) && k != table.PrimaryId) continue;
            o[k] = v switch
            {
                null => null,
                DateTimeOffset d => d.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                Guid g => g.ToString(),
                string s => s,
                int i => i,
                long l => l,
                decimal m => m,
                bool b => b,
                _ => v.ToString(),
            };
        }
        return o;
    }

    public static string ETag(Row row) => $"W/\"{row.Version}\"";

    // --- reads ------------------------------------------------------------------------------------

    public (TableMeta Table, Row? Row) Retrieve(string set, RowRef reference)
    {
        var table = Table(set);
        return (table, Find(table, reference));
    }

    private Row? Find(TableMeta table, RowRef reference) =>
        reference.Id is { } id ? store.Get(table.EntitySet, id) : store.FindByKey(table.EntitySet, reference.Keys!.Keys.ToArray(), reference.Keys!);

    public IReadOnlyList<Row> Query(string set, string? filter, string? orderBy)
    {
        var table = Table(set);
        IEnumerable<Row> rows = store.Rows(set);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var predicate = ODataFilter.Parse(filter, table);
            rows = rows.Where(predicate);
        }
        var list = rows.ToList();
        if (!string.IsNullOrWhiteSpace(orderBy))
        {
            IOrderedEnumerable<Row>? ordered = null;
            foreach (var clause in orderBy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
            {
                var parts = clause.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var col = parts[0];
                if (!table.HasColumn(col)) throw DataverseException.Invalid($"$orderby: unknown column {col}.");
                var desc = parts.Length > 1 && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase);
                var comparer = Comparer<object?>.Create(ODataFilter.Compare);
                list = (desc ? list.OrderByDescending(r => r.Values.GetValueOrDefault(col), comparer) : list.OrderBy(r => r.Values.GetValueOrDefault(col), comparer)).ToList();
                ordered = null;
            }
            _ = ordered;
        }
        else
        {
            list = list.OrderBy(r => r.Values.GetValueOrDefault(table.PrimaryId) as Guid? ?? Guid.Empty).ToList();
        }
        return list;
    }

    // --- writes -----------------------------------------------------------------------------------

    public sealed record WriteResult(TableMeta Table, Row Row, bool Created);

    public WriteResult Create(Store.Transaction tx, Caller caller, string set, Dictionary<string, object?> values)
    {
        var table = Table(set);
        CheckColumnSecurity(table, caller, values.Keys);
        var target = new RecordAdapter(values);
        if (table.LogicalName == Model.Account) RunPluginRule(() => ValidateMerchantLogic.Execute(target, null));

        var id = values.GetValueOrDefault(table.PrimaryId) as Guid? ?? Guid.NewGuid();
        var now = clock.GetUtcNow();
        var row = new Row(new Dictionary<string, object?>(StringComparer.Ordinal));
        foreach (var c in table.Columns.Keys) row.Values[c] = null;
        foreach (var (k, v) in values) row.Values[k] = v;
        row.Values[table.PrimaryId] = id;
        row.Values["createdon"] = now;
        row.Values["modifiedon"] = now;
        row.Values["_createdby_value"] = caller.UserId;
        row.Values["_modifiedby_value"] = caller.UserId;
        if (table.LogicalName == Model.Account && row.Values.GetValueOrDefault(Model.LifecycleStage) is null) row.Values[Model.LifecycleStage] = Model.Stage.Prospect;
        row.Version = store.NextVersion();
        row.Values["versionnumber"] = row.Version;
        tx.Insert(set, id, row);
        return new WriteResult(table, row, true);
    }

    /// <summary>PATCH semantics: update, or create when the row does not exist (upsert), governed by
    /// If-Match / If-None-Match exactly as the Dataverse Web API documents.</summary>
    public WriteResult Patch(Store.Transaction tx, Caller caller, string set, RowRef reference, Dictionary<string, object?> values, string? ifMatch, string? ifNoneMatch)
    {
        var table = Table(set);
        var existing = Find(table, reference);
        if (existing is null)
        {
            if (ifMatch is not null) throw new DataverseException(404, DataverseException.NotFound, $"{table.LogicalName} with the given key does not exist.");
            var create = new Dictionary<string, object?>(values, StringComparer.Ordinal);
            if (reference.Id is { } id) create[table.PrimaryId] = id;
            if (reference.Keys is not null) foreach (var (k, v) in reference.Keys) create.TryAdd(k, v);
            return Create(tx, caller, set, create);
        }
        if (ifNoneMatch == "*") throw new DataverseException(412, DataverseException.DuplicateKey, $"A record with matching key values already exists ({table.LogicalName}).");
        if (ifMatch is not null && ifMatch != "*" && ifMatch != ETag(existing))
            throw new DataverseException(412, DataverseException.ConcurrencyMismatch, "The version of the existing record doesn't match the RowVersion property provided.");

        // As in Dataverse, alternate key values in the URL are not changed by the body.
        var changes = new Dictionary<string, object?>(values, StringComparer.Ordinal);
        if (reference.Keys is not null) foreach (var k in reference.Keys.Keys) changes.Remove(k);
        changes.Remove(table.PrimaryId);
        Update(tx, caller, table, existing, changes);
        return new WriteResult(table, existing, false);
    }

    private void Update(Store.Transaction tx, Caller caller, TableMeta table, Row existing, Dictionary<string, object?> changes)
    {
        CheckColumnSecurity(table, caller, changes.Keys);
        if (table.LogicalName == Model.Account)
        {
            var target = new RecordAdapter(changes);
            var pre = new RecordAdapter(existing.Values);
            if (changes.Keys.Any(k => k is Model.Iban or Model.Mcc or Model.Country or Model.MerchantId))
                RunPluginRule(() => ValidateMerchantLogic.Execute(target, pre));
            if (changes.Keys.Any(k => Model.SyncedCrmColumns.Any(c => c.Key == k)))
            {
                var context = new PluginContext(caller.UserId, options.IntegrationUserId, Guid.NewGuid);
                RunPluginRule(() => MerchantOutboxLogic.Execute(target, pre, context, new OutboxSink(this, tx)));
            }
        }
        var now = clock.GetUtcNow();
        changes["modifiedon"] = now;
        changes["_modifiedby_value"] = caller.UserId;
        var version = store.NextVersion();
        changes["versionnumber"] = version;
        tx.Update(table.EntitySet, (Guid)existing.Values[table.PrimaryId]!, existing, changes, version);
    }

    public void Delete(Store.Transaction tx, Caller caller, string set, RowRef reference, string? ifMatch)
    {
        var table = Table(set);
        var existing = Find(table, reference) ?? throw new DataverseException(404, DataverseException.NotFound, $"{table.LogicalName} does not exist.");
        if (ifMatch is not null && ifMatch != "*" && ifMatch != ETag(existing))
            throw new DataverseException(412, DataverseException.ConcurrencyMismatch, "The version of the existing record doesn't match the RowVersion property provided.");
        if (!caller.IsIntegration && !caller.IsSystem && table.Columns.Values.Any(c => c.Owner == "bridge"))
            throw new DataverseException(403, DataverseException.Forbidden, $"Principal user is missing prvDelete{table.LogicalName}.");
        tx.Delete(set, (Guid)existing.Values[table.PrimaryId]!);
    }

    /// <summary>The bound custom API mcb_RequestOnboarding: the plug-in's rules, its update through the
    /// normal Update pipeline, and the outbox row, in one transaction.</summary>
    public JsonObject RequestOnboarding(Store.Transaction tx, Caller caller, RowRef account)
    {
        var table = Table("accounts");
        var row = Find(table, account) ?? throw new DataverseException(404, DataverseException.NotFound, "account does not exist.");
        var update = new RecordAdapter(new Dictionary<string, object?>(StringComparer.Ordinal));
        var context = new PluginContext(caller.UserId, options.IntegrationUserId, Guid.NewGuid);
        OutboxEntry entry = null!;
        RunPluginRule(() => entry = RequestOnboardingLogic.Execute(new RecordAdapter(row.Values), update, context, new OutboxSink(this, tx)));
        // The plug-in updates the account with the system service (KYC columns are secured for people).
        Update(tx, Caller.System, table, row, update.Values);
        return new JsonObject { ["EventId"] = entry.EventId };
    }

    private void CheckColumnSecurity(TableMeta table, Caller caller, IEnumerable<string> columns)
    {
        if (caller.IsIntegration || caller.IsSystem) return;
        foreach (var c in columns)
        {
            if (table.Columns.TryGetValue(c, out var meta) && meta.Owner is "platform" or "bridge" or "crm-system")
                throw new DataverseException(403, DataverseException.Forbidden, $"The user does not have update permissions to the secured field '{c}' on {table.LogicalName}.");
        }
    }

    private static void RunPluginRule(Action action)
    {
        try
        {
            action();
        }
        catch (PluginRuleException ex)
        {
            throw new DataverseException(400, DataverseException.PluginAbort, ex.Message);
        }
    }

    private sealed class OutboxSink(Engine engine, Store.Transaction tx) : IOutboxSink
    {
        public void Write(OutboxEntry entry)
        {
            engine.Create(tx, Caller.System, "mcb_outboxmessages", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["mcb_name"] = $"{entry.EventType} {entry.MerchantId} #{entry.Sequence}",
                ["mcb_eventid"] = entry.EventId,
                ["mcb_eventtype"] = entry.EventType,
                ["mcb_merchantid"] = entry.MerchantId,
                ["mcb_sequence"] = entry.Sequence,
                ["mcb_schemaversion"] = entry.SchemaVersion,
                ["mcb_payload"] = entry.PayloadJson,
                ["mcb_status"] = 771000500,
                ["mcb_attempts"] = 0,
            });
        }
    }

    /// <summary>IRecord over a dictionary: what the plug-in logic sees.</summary>
    public sealed class RecordAdapter(Dictionary<string, object?> values) : IRecord
    {
        public Dictionary<string, object?> Values { get; } = values;
        public bool Contains(string column) => Values.ContainsKey(column);
        public object? Get(string column) => Values.GetValueOrDefault(column);
        public void Set(string column, object? value) => Values[column] = value;
    }
}
