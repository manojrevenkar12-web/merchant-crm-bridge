using System.Diagnostics;
using System.Text.Json.Nodes;
using Bridge.Core.Contracts;
using Bridge.Core.Observability;
using Bridge.Core.Ports;

namespace Bridge.Core.Services;

public sealed class RelayOptions
{
    /// <summary>CloudEvents source for CRM events, e.g. the Dataverse environment URL.</summary>
    public string Source { get; set; } = "dynamics365://merchant-crm";
    public int BatchSize { get; set; } = 100;
    public int MaxAttempts { get; set; } = 5;
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(1);
    public string Holder { get; set; } = Environment.MachineName + "/" + Environment.ProcessId;
}

public sealed record RelayReport(int Published, int Failed, int HeldBack, bool LeaseHeld);

/// <summary>
/// Publishes the Dataverse outbox to Kafka in the order the changes committed. The plug-in wrote each
/// row in the same transaction as the change, so a change that committed is never lost; if the relay
/// dies after Kafka acknowledged but before the row is marked sent, the event is published again with the
/// same id, which consumers deduplicate. When a merchant's message fails, that merchant's later messages
/// wait, so the platform never sees them out of order. One relay runs at a time (a lease row in Dataverse).
/// </summary>
public sealed class OutboxRelay(ICrmGateway crm, IEventLog log, SchemaRegistry schemas, TimeProvider clock, RelayOptions options, BridgeTelemetry telemetry)
{
    public async Task<RelayReport> RunOnceAsync(CancellationToken ct)
    {
        if (!await crm.TryAcquireLeaseAsync("outbox-relay", options.Holder, options.LeaseDuration, ct).ConfigureAwait(false))
            return new RelayReport(0, 0, 0, false);

        var pending = await crm.GetPendingOutboxAsync(options.BatchSize, ct).ConfigureAwait(false);
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        int published = 0, failed = 0, heldBack = 0;

        foreach (var message in pending.OrderBy(m => m.CreatedOn).ThenBy(m => m.Sequence))
        {
            if (blocked.Contains(message.MerchantId)) { heldBack++; continue; }
            if (message.Attempts >= options.MaxAttempts)
            {
                // Stuck: it stays pending and visible in the operations console; later changes of this merchant wait for a person.
                blocked.Add(message.MerchantId);
                heldBack++;
                continue;
            }
            using var activity = BridgeTelemetry.Activities.StartActivity("outbox publish", ActivityKind.Producer);
            activity?.SetTag("cloudevents.event_id", message.EventId);
            try
            {
                var ce = ToCloudEvent(message);
                schemas.Validate(ce.Type, ce.SchemaVersion, ce.Data);
                await log.ProduceAsync(Topics.CrmEvents, message.MerchantId, ce.Serialize(), TraceContext.Inject(activity), ct).ConfigureAwait(false);
                await crm.MarkOutboxSentAsync(message, clock.GetUtcNow(), ct).ConfigureAwait(false);
                telemetry.Published.Add(1, new KeyValuePair<string, object?>("type", message.EventType));
                published++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                blocked.Add(message.MerchantId);
                failed++;
                telemetry.PublishFailures.Add(1);
                try { await crm.MarkOutboxFailedAsync(message, ex.GetType().Name + ": " + ex.Message, ct).ConfigureAwait(false); }
                catch (Exception) when (!ct.IsCancellationRequested) { /* the attempt count is best effort; the row stays pending either way */ }
            }
        }
        return new RelayReport(published, failed, heldBack, true);
    }

    public CloudEvent ToCloudEvent(OutboxMessage m)
    {
        JsonNode data;
        try
        {
            data = JsonNode.Parse(m.PayloadJson) ?? throw new ContractException("empty payload");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new ContractException("outbox payload is not JSON: " + ex.Message, ex);
        }
        return new CloudEvent
        {
            Id = m.EventId,
            Source = options.Source,
            Type = m.EventType,
            Subject = m.MerchantId,
            Time = m.CreatedOn,
            Sequence = m.Sequence,
            SchemaVersion = m.SchemaVersion,
            DataSchema = CloudEvent.SchemaUri(m.EventType, m.SchemaVersion),
            Data = data,
        };
    }
}
