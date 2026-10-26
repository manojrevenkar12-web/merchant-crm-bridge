using System.Diagnostics;
using Bridge.Core.Contracts;
using Bridge.Core.Domain;
using Bridge.Core.Observability;
using Bridge.Core.Ports;

namespace Bridge.Core.Services;

public sealed class ConsumerOptions
{
    public string GroupId { get; set; } = "merchant-crm-bridge";
    public int UnknownMerchantAttempts { get; set; } = 3;
    public int MaxDeliveriesBeforeDeadLetter { get; set; } = 8;
    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>What happened to one message.</summary>
public enum Handling
{
    Committed,
    DeadLettered,
    RetryLater,
}

public sealed record HandledMessage(Handling Handling, Outcome? Outcome, string Detail, TimeSpan Delay);

/// <summary>
/// Reads platform events from Kafka and applies them. A message that breaks the contract goes to the
/// dead-letter topic at once; a transient failure (throttling, 5xx, a merchant row that keeps changing)
/// rewinds the partition and waits, so one merchant's events are never applied out of the order they
/// were delivered in; anything that keeps failing goes to the dead-letter topic after a bounded number of
/// deliveries, so one bad message cannot stop a partition for good.
/// </summary>
public sealed class PlatformConsumer(IEventLog log, PlatformEventApplier applier, EventReader reader, ConsumerOptions options, BridgeTelemetry telemetry)
{
    public async Task<HandledMessage> HandleAsync(IEventConsumer consumer, LogMessage message, CancellationToken ct)
    {
        var parent = TraceContext.Extract(message.Headers);
        using var activity = BridgeTelemetry.Activities.StartActivity("platform-event apply", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.kafka.partition", message.Partition);
        activity?.SetTag("messaging.kafka.offset", message.Offset);

        CloudEvent ce;
        PlatformEvent evt;
        try
        {
            ce = CloudEvent.Parse(message.Value);
            evt = reader.Read(ce);
            if (ce.Subject != message.Key) throw new ContractException($"Kafka key {message.Key} differs from subject {ce.Subject}");
        }
        catch (ContractException ex)
        {
            return await DeadLetterAsync(consumer, message, "contract", ex.Message, ct).ConfigureAwait(false);
        }
        activity?.SetTag("cloudevents.event_id", ce.Id);
        activity?.SetTag("cloudevents.event_type", ce.Type);

        try
        {
            var result = await applier.ApplyAsync(ce, evt, ct).ConfigureAwait(false);
            consumer.Commit(message);
            return new HandledMessage(Handling.Committed, result.Outcome, result.Detail, TimeSpan.Zero);
        }
        catch (UnknownMerchantException ex)
        {
            if (message.DeliveryCount >= options.UnknownMerchantAttempts)
                return await DeadLetterAsync(consumer, message, "unknown_merchant", ex.Message, ct).ConfigureAwait(false);
            return Retry(consumer, message, ex.Message, null);
        }
        catch (TransientException ex)
        {
            if (ex.RetryAfter is not null) telemetry.Throttled.Add(1);
            // Throttling and outages are waited out, however long: dead-lettering them would reorder the merchant's events.
            return Retry(consumer, message, ex.Message, ex.RetryAfter);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (message.DeliveryCount >= options.MaxDeliveriesBeforeDeadLetter)
                return await DeadLetterAsync(consumer, message, "error", ex.GetType().Name + ": " + ex.Message, ct).ConfigureAwait(false);
            return Retry(consumer, message, ex.Message, null);
        }
    }

    private HandledMessage Retry(IEventConsumer consumer, LogMessage message, string why, TimeSpan? retryAfter)
    {
        consumer.Rewind(message);
        var backoff = retryAfter ?? Backoff(message.DeliveryCount);
        return new HandledMessage(Handling.RetryLater, null, why, backoff);
    }

    public TimeSpan Backoff(int deliveryCount)
    {
        var exp = options.BaseBackoff.TotalMilliseconds * Math.Pow(2, Math.Max(0, deliveryCount - 1));
        return TimeSpan.FromMilliseconds(Math.Min(exp, options.MaxBackoff.TotalMilliseconds));
    }

    private async Task<HandledMessage> DeadLetterAsync(IEventConsumer consumer, LogMessage message, string reason, string detail, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(message.Headers)
        {
            ["dlq-reason"] = reason,
            ["dlq-detail"] = detail.Length > 500 ? detail[..500] : detail,
            ["dlq-source"] = $"{message.Topic}/{message.Partition}/{message.Offset}",
            ["dlq-deliveries"] = message.DeliveryCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        await log.ProduceAsync(Topics.PlatformEventsDlq, message.Key, message.Value, headers, ct).ConfigureAwait(false);
        consumer.Commit(message);
        telemetry.DeadLettered.Add(1, new KeyValuePair<string, object?>("reason", reason));
        return new HandledMessage(Handling.DeadLettered, null, reason + ": " + detail, TimeSpan.Zero);
    }

    /// <summary>The consume loop for a host: handle, wait when told to, until cancelled.</summary>
    public async Task RunAsync(IEventConsumer consumer, TimeProvider clock, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            LogMessage? message;
            try
            {
                message = await consumer.ConsumeAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (message is null) continue;
            var handled = await HandleAsync(consumer, message, ct).ConfigureAwait(false);
            if (handled.Delay > TimeSpan.Zero)
            {
                try { await Task.Delay(handled.Delay, clock, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
