using Bridge.Core.Contracts;
using Bridge.Core.Ports;

namespace Bridge.PlatformSim;

/// <summary>Feeds CRM events from Kafka into the simulator (at-least-once; the simulator deduplicates).</summary>
public sealed class CrmEventPump(IEventLog log, Simulator simulator, string groupId = "payments-platform")
{
    public async Task RunAsync(CancellationToken ct)
    {
        await using var consumer = log.Subscribe(groupId, [Topics.CrmEvents], "platform");
        while (!ct.IsCancellationRequested)
        {
            LogMessage? m;
            try { m = await consumer.ConsumeAsync(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (m is null) continue;
            try { simulator.HandleCrmEvent(CloudEvent.Parse(m.Value)); }
            catch (ContractException) { /* the platform's own dead-letter handling is out of scope here */ }
            consumer.Commit(m);
        }
    }

    /// <summary>Handles everything currently on the topic, then returns (drills step the world).</summary>
    public async Task<int> DrainAsync(IEventConsumer consumer, CancellationToken ct)
    {
        var n = 0;
        while (await consumer.ConsumeAsync(TimeSpan.FromMilliseconds(30), ct).ConfigureAwait(false) is { } m)
        {
            try { simulator.HandleCrmEvent(CloudEvent.Parse(m.Value)); }
            catch (ContractException) { }
            consumer.Commit(m);
            n++;
        }
        return n;
    }
}
