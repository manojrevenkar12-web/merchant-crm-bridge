using System;
using System.Collections.Generic;

namespace MerchantCrmBridge.Plugins.Logic;

/// <summary>A row as the plug-in sees it. Choices are their integer value, money is decimal.</summary>
public interface IRecord
{
    bool Contains(string column);
    object? Get(string column);
    void Set(string column, object? value);
}

/// <summary>Writes an outbox row in the plug-in's transaction (IOrganizationService.Create in Dataverse).</summary>
public interface IOutboxSink
{
    void Write(OutboxEntry entry);
}

public sealed class OutboxEntry
{
    public OutboxEntry(string eventId, string eventType, string merchantId, long sequence, int schemaVersion, string payloadJson)
    {
        EventId = eventId;
        EventType = eventType;
        MerchantId = merchantId;
        Sequence = sequence;
        SchemaVersion = schemaVersion;
        PayloadJson = payloadJson;
    }

    public string EventId { get; }
    public string EventType { get; }
    public string MerchantId { get; }
    public long Sequence { get; }
    public int SchemaVersion { get; }
    public string PayloadJson { get; }
}

public sealed class PluginContext
{
    public PluginContext(Guid initiatingUserId, Guid? integrationUserId, Func<Guid> newId)
    {
        InitiatingUserId = initiatingUserId;
        IntegrationUserId = integrationUserId;
        NewId = newId;
    }

    public Guid InitiatingUserId { get; }

    /// <summary>The bridge's application user (environment variable mcb_IntegrationUserId).</summary>
    public Guid? IntegrationUserId { get; }

    public Func<Guid> NewId { get; }

    public bool IsBridge => IntegrationUserId.HasValue && IntegrationUserId.Value == InitiatingUserId;
}

/// <summary>Thrown for a business rule; the Dataverse adapter turns it into InvalidPluginExecutionException.</summary>
public sealed class PluginRuleException : Exception
{
    public PluginRuleException(string message) : base(message) { }
    public PluginRuleException() { }
    public PluginRuleException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Reads a column from the target, falling back to the pre-image: the row after the change.</summary>
public sealed class MergedRecord
{
    private readonly IRecord _target;
    private readonly IRecord? _pre;

    public MergedRecord(IRecord target, IRecord? pre)
    {
        _target = target;
        _pre = pre;
    }

    public object? Get(string column) => _target.Contains(column) ? _target.Get(column) : _pre?.Get(column);

    public string? GetString(string column) => Get(column) as string;

    public int? GetInt(string column) => Get(column) is int i ? i : (int?)null;

    public long GetLong(string column)
    {
        var v = Get(column);
        if (v is long l) return l;
        if (v is int i) return i;
        return 0;
    }

    public bool Changed(string column)
    {
        if (!_target.Contains(column)) return false;
        var before = _pre?.Get(column);
        var after = _target.Get(column);
        return !Equals(Normalize(before), Normalize(after));
    }

    private static object? Normalize(object? v) => v is string s && s.Length == 0 ? null : v;

    public IReadOnlyList<string> ChangedColumns(IEnumerable<string> columns)
    {
        var list = new List<string>();
        foreach (var c in columns) if (Changed(c)) list.Add(c);
        return list;
    }
}
