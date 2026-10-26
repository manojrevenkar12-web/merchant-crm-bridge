using System.Text;
using System.Text.Json.Nodes;
using Bridge.Core.Contracts;
using Bridge.Core.Domain;
using Bridge.Core.Messaging;
using Bridge.Core.Observability;
using Bridge.Core.Services;
using Bridge.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Bridge.Tests;

/// <summary>The consumer under the Functions Kafka trigger: the host retries an invocation that throws and
/// checkpoints one that returns. These tests play the host.</summary>
public class TriggerDeliveryTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-10-02T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly BridgeTelemetry _telemetry = new();

    private static Dictionary<string, object?> Binding(string key, int partition = 2, long offset = 41, Dictionary<string, string>? headers = null) => new()
    {
        ["Key"] = key,
        ["Partition"] = partition,
        ["Offset"] = offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Topic"] = Topics.PlatformEvents,
        ["Headers"] = new JsonArray((headers ?? []).Select(h => (JsonNode)new JsonObject { ["Key"] = h.Key, ["Value"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(h.Value)) }).ToArray()).ToJsonString(),
    };

    /// <summary>Invokes like the host with an unbounded retry policy; returns how many invocations it took.</summary>
    private async Task<int> HostAsync(PlatformConsumer consumer, string value, Dictionary<string, object?> binding, int maxInvocations = 20)
    {
        for (var retry = 0; retry < maxInvocations; retry++)
        {
            var message = TriggerDelivery.Message(value, binding, retry, Topics.PlatformEvents);
            var invocation = new TriggerDelivery();
            var handled = await consumer.HandleAsync(invocation, message, _ct);
            try
            {
                await TriggerDelivery.RetryIfAskedAsync(handled, TimeSpan.Zero, _clock, _ct);
                Assert.True(invocation.Committed);
                return retry + 1;
            }
            catch (DeliverAgainException)
            {
                Assert.True(invocation.RewindRequested);
            }
        }
        throw new InvalidOperationException("never checkpointed");
    }

    [Fact]
    public void Binding_data_becomes_the_same_message_the_kafka_consumer_sees()
    {
        var m = TriggerDelivery.Message("{}", Binding("M-000001", 3, 1234, new() { ["traceparent"] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01" }), retryCount: 2, Topics.PlatformEvents);
        Assert.Equal((Topics.PlatformEvents, 3, 1234L, "M-000001", 3), (m.Topic, m.Partition, m.Offset, m.Key, m.DeliveryCount));
        Assert.Equal("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01", m.Headers["traceparent"]);
    }

    [Fact]
    public async Task An_event_applies_once_and_checkpoints_on_the_first_invocation()
    {
        var crm = new FakeCrm();
        crm.Add("M-000001");
        var log = new InMemoryEventLog(1);
        var consumer = new PlatformConsumer(log, new PlatformEventApplier(crm, _clock, _telemetry), EventReader.Default, new ConsumerOptions(), _telemetry);
        var kyc = Events.Kyc("M-000001", 1, "approved").Serialize();
        Assert.Equal(1, await HostAsync(consumer, kyc, Binding("M-000001")));
        Assert.Equal(1, await HostAsync(consumer, kyc, Binding("M-000001"))); // redelivered after a rebalance: a duplicate
        Assert.Single(crm.Ledger);
        Assert.Equal(LifecycleStage.Approved, crm.Merchants["M-000001"].Stage);
    }

    [Fact]
    public async Task An_unknown_merchant_is_retried_by_the_host_then_dead_lettered()
    {
        var log = new InMemoryEventLog(1);
        var consumer = new PlatformConsumer(log, new PlatformEventApplier(new FakeCrm(), _clock, _telemetry), EventReader.Default, new ConsumerOptions { UnknownMerchantAttempts = 3 }, _telemetry);
        Assert.Equal(3, await HostAsync(consumer, Events.Kyc("M-000404", 1, "approved").Serialize(), Binding("M-000404")));
        var dlq = Assert.Single(await log.ReadTopicAsync(Topics.PlatformEventsDlq, 10, _ct));
        Assert.Equal(("unknown_merchant", "3", $"{Topics.PlatformEvents}/2/41"), (dlq.Headers["dlq-reason"], dlq.Headers["dlq-deliveries"], dlq.Headers["dlq-source"]));
    }

    [Fact]
    public async Task A_broken_event_is_dead_lettered_without_a_retry()
    {
        var log = new InMemoryEventLog(1);
        var crm = new FakeCrm();
        crm.Add("M-000001");
        var consumer = new PlatformConsumer(log, new PlatformEventApplier(crm, _clock, _telemetry), EventReader.Default, new ConsumerOptions(), _telemetry);
        Assert.Equal(1, await HostAsync(consumer, "{not json", Binding("M-000001", headers: new() { ["traceparent"] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01" })));
        var dlq = Assert.Single(await log.ReadTopicAsync(Topics.PlatformEventsDlq, 10, _ct));
        Assert.Equal("contract", dlq.Headers["dlq-reason"]);
        Assert.Equal("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01", dlq.Headers["traceparent"]); // the trace continues into the dead letter
    }

    [Fact]
    public async Task Throttling_holds_the_partition_and_never_dead_letters()
    {
        var crm = new ThrottlingGateway(throttleTimes: 12);
        crm.Inner.Add("M-000001");
        var log = new InMemoryEventLog(1);
        var consumer = new PlatformConsumer(log, new PlatformEventApplier(crm, _clock, _telemetry), EventReader.Default, new ConsumerOptions { MaxDeliveriesBeforeDeadLetter = 8 }, _telemetry);
        Assert.Equal(13, await HostAsync(consumer, Events.Kyc("M-000001", 1, "approved").Serialize(), Binding("M-000001")));
        Assert.Empty(await log.ReadTopicAsync(Topics.PlatformEventsDlq, 10, _ct));
        Assert.Equal("approved", crm.Inner.Merchants["M-000001"].KycDecision);
    }

    private sealed class ThrottlingGateway(int throttleTimes) : Bridge.Core.Ports.ICrmGateway
    {
        private int _left = throttleTimes;
        public FakeCrm Inner { get; } = new();

        public Task<CrmMerchant?> GetMerchantAsync(string merchantId, CancellationToken ct) =>
            _left-- > 0 ? throw new Bridge.Core.Ports.TransientException("429", TimeSpan.FromSeconds(7)) : Inner.GetMerchantAsync(merchantId, ct);

        public Task<bool> InboundEventSeenAsync(string eventId, CancellationToken ct) => Inner.InboundEventSeenAsync(eventId, ct);
        public Task CommitAsync(CrmMerchant read, Effect effect, Bridge.Core.Ports.InboundRecord ledger, CancellationToken ct) => Inner.CommitAsync(read, effect, ledger, ct);
        public Task<IReadOnlyList<Bridge.Core.Ports.OutboxMessage>> GetPendingOutboxAsync(int max, CancellationToken ct) => Inner.GetPendingOutboxAsync(max, ct);
        public Task MarkOutboxSentAsync(Bridge.Core.Ports.OutboxMessage message, DateTimeOffset at, CancellationToken ct) => Inner.MarkOutboxSentAsync(message, at, ct);
        public Task MarkOutboxFailedAsync(Bridge.Core.Ports.OutboxMessage message, string error, CancellationToken ct) => Inner.MarkOutboxFailedAsync(message, error, ct);
        public Task<Bridge.Core.Ports.CrmPage<CrmMerchant>> ListSyncedMerchantsAsync(string? continuation, int pageSize, CancellationToken ct) => Inner.ListSyncedMerchantsAsync(continuation, pageSize, ct);
        public Task WriteDriftAsync(Bridge.Core.Ports.DriftFinding finding, CancellationToken ct) => Inner.WriteDriftAsync(finding, ct);
        public Task<bool> TryAcquireLeaseAsync(string name, string holder, TimeSpan duration, CancellationToken ct) => Inner.TryAcquireLeaseAsync(name, holder, duration, ct);
    }
}
