using System.Text.Json.Nodes;
using Bridge.Core.Contracts;
using Bridge.Core.Domain;
using Bridge.Core.Messaging;
using Bridge.Core.Observability;
using Bridge.Core.Ports;
using Bridge.Core.Services;
using Microsoft.Extensions.Time.Testing;

namespace Bridge.Tests;

public class ServiceTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-10-02T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly BridgeTelemetry _telemetry = new();

    private (FakeCrm Crm, InMemoryEventLog Log, PlatformConsumer Consumer, IEventConsumer Kafka) Setup(int unknownAttempts = 3)
    {
        var crm = new FakeCrm();
        var log = new InMemoryEventLog(partitions: 2);
        var applier = new PlatformEventApplier(crm, _clock, _telemetry);
        var consumer = new PlatformConsumer(log, applier, EventReader.Default, new ConsumerOptions { UnknownMerchantAttempts = unknownAttempts }, _telemetry);
        return (crm, log, consumer, log.Subscribe("bridge", [Topics.PlatformEvents], "test"));
    }

    private async Task<List<HandledMessage>> Drain(IEventConsumer kafka, PlatformConsumer consumer, int max = 100)
    {
        var handled = new List<HandledMessage>();
        for (var i = 0; i < max && await kafka.ConsumeAsync(TimeSpan.FromMilliseconds(20), _ct) is { } m; i++)
            handled.Add(await consumer.HandleAsync(kafka, m, _ct));
        return handled;
    }

    private Task Publish(InMemoryEventLog log, CloudEvent ce) => log.ProduceAsync(Topics.PlatformEvents, ce.Subject, ce.Serialize(), null, _ct);

    [Fact]
    public async Task A_duplicate_delivery_is_recorded_once_and_applied_once()
    {
        var (crm, log, consumer, kafka) = Setup();
        crm.Add("M-000001");
        var kyc = Events.Kyc("M-000001", 1, "approved");
        await Publish(log, kyc);
        await Publish(log, kyc);
        var handled = await Drain(kafka, consumer);
        Assert.Equal([Outcome.Applied, Outcome.Duplicate], handled.Select(h => h.Outcome!.Value));
        Assert.Single(crm.Ledger);
        Assert.Equal(1, crm.Commits);
        Assert.Equal(LifecycleStage.Approved, crm.Merchants["M-000001"].Stage);
    }

    [Fact]
    public async Task Broken_messages_go_to_the_dead_letter_topic_with_the_reason()
    {
        var (crm, log, consumer, kafka) = Setup();
        crm.Add("M-000001");
        await log.ProduceAsync(Topics.PlatformEvents, "M-000001", "{not json", null, _ct);
        await Publish(log, Events.Platform(EventTypes.KycDecided, "M-000001", 1, new JsonObject { ["decision"] = "perhaps" }));
        await log.ProduceAsync(Topics.PlatformEvents, "M-000002", Events.Kyc("M-000001", 2, "approved").Serialize(), null, _ct); // key differs from subject
        var handled = await Drain(kafka, consumer);
        Assert.All(handled, h => Assert.Equal(Handling.DeadLettered, h.Handling));
        var dlq = await log.ReadTopicAsync(Topics.PlatformEventsDlq, 10, _ct);
        Assert.Equal(3, dlq.Count);
        Assert.All(dlq, m => Assert.Equal("contract", m.Headers["dlq-reason"]));
        Assert.Contains(dlq, m => m.Headers["dlq-detail"].Contains("schema", StringComparison.Ordinal));
        Assert.Empty(crm.Ledger);
    }

    [Fact]
    public async Task An_unknown_merchant_is_retried_then_dead_lettered()
    {
        var (_, log, consumer, kafka) = Setup(unknownAttempts: 3);
        await Publish(log, Events.Kyc("M-000404", 1, "approved"));
        var handled = await Drain(kafka, consumer);
        Assert.Equal([Handling.RetryLater, Handling.RetryLater, Handling.DeadLettered], handled.Select(h => h.Handling));
        Assert.True(handled[1].Delay > handled[0].Delay); // exponential backoff
        var dlq = Assert.Single(await log.ReadTopicAsync(Topics.PlatformEventsDlq, 10, _ct));
        Assert.Equal("unknown_merchant", dlq.Headers["dlq-reason"]);
    }

    [Fact]
    public async Task A_concurrent_write_forces_a_reread_and_the_event_still_applies()
    {
        var (crm, log, consumer, kafka) = Setup();
        crm.Add("M-000001");
        var interfered = false;
        crm.BeforeCommit = m =>
        {
            if (interfered) return null;
            interfered = true;
            return m with { RiskBand = "low", RiskVersion = 1 }; // someone else wrote the row after it was read
        };
        await Publish(log, Events.Kyc("M-000001", 2, "approved"));
        var handled = await Drain(kafka, consumer);
        Assert.Equal(Outcome.Applied, Assert.Single(handled).Outcome);
        Assert.Equal("approved", crm.Merchants["M-000001"].KycDecision);
        Assert.Equal("low", crm.Merchants["M-000001"].RiskBand); // the other writer's change survives
    }

    [Fact]
    public async Task Throttling_rewinds_and_waits_for_retry_after_without_dead_lettering()
    {
        var crm = new ThrottlingCrm(throttleTimes: 2);
        crm.Inner.Add("M-000001");
        var log = new InMemoryEventLog(1);
        var consumer = new PlatformConsumer(log, new PlatformEventApplier(crm, _clock, _telemetry), EventReader.Default, new ConsumerOptions(), _telemetry);
        await using var kafka = log.Subscribe("bridge", [Topics.PlatformEvents], "t");
        await Publish(log, Events.Kyc("M-000001", 1, "approved"));
        var handled = await Drain(kafka, consumer);
        Assert.Equal([Handling.RetryLater, Handling.RetryLater, Handling.Committed], handled.Select(h => h.Handling));
        Assert.Equal(TimeSpan.FromSeconds(7), handled[0].Delay);
        Assert.Empty(await log.ReadTopicAsync(Topics.PlatformEventsDlq, 10, _ct));
    }

    private sealed class ThrottlingCrm(int throttleTimes) : ICrmGateway
    {
        private int _left = throttleTimes;
        public FakeCrm Inner { get; } = new();
        public Task<CrmMerchant?> GetMerchantAsync(string merchantId, CancellationToken ct) =>
            _left-- > 0 ? throw new TransientException("429", TimeSpan.FromSeconds(7)) : Inner.GetMerchantAsync(merchantId, ct);
        public Task<bool> InboundEventSeenAsync(string eventId, CancellationToken ct) => Inner.InboundEventSeenAsync(eventId, ct);
        public Task CommitAsync(CrmMerchant read, Effect effect, InboundRecord ledger, CancellationToken ct) => Inner.CommitAsync(read, effect, ledger, ct);
        public Task<IReadOnlyList<OutboxMessage>> GetPendingOutboxAsync(int max, CancellationToken ct) => Inner.GetPendingOutboxAsync(max, ct);
        public Task MarkOutboxSentAsync(OutboxMessage message, DateTimeOffset at, CancellationToken ct) => Inner.MarkOutboxSentAsync(message, at, ct);
        public Task MarkOutboxFailedAsync(OutboxMessage message, string error, CancellationToken ct) => Inner.MarkOutboxFailedAsync(message, error, ct);
        public Task<CrmPage<CrmMerchant>> ListSyncedMerchantsAsync(string? continuation, int pageSize, CancellationToken ct) => Inner.ListSyncedMerchantsAsync(continuation, pageSize, ct);
        public Task WriteDriftAsync(DriftFinding finding, CancellationToken ct) => Inner.WriteDriftAsync(finding, ct);
        public Task<bool> TryAcquireLeaseAsync(string name, string holder, TimeSpan duration, CancellationToken ct) => Inner.TryAcquireLeaseAsync(name, holder, duration, ct);
    }

    private static OutboxMessage Outbox(string merchant, long seq, string payload = "{\"merchantId\":\"MID\",\"changes\":{\"pricingPlan\":\"growth\"}}", int attempts = 0) =>
        new(Guid.NewGuid(), "W/\"1\"", Guid.NewGuid().ToString(), EventTypes.DetailsChanged, merchant, seq, 1, payload.Replace("MID", merchant, StringComparison.Ordinal),
            DateTimeOffset.Parse("2026-10-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture).AddMinutes(seq), attempts);

    [Fact]
    public async Task The_relay_publishes_in_commit_order_and_holds_back_a_merchant_after_a_failure()
    {
        var crm = new FakeCrm();
        crm.Outbox.AddRange([Outbox("M-000001", 1), Outbox("M-000002", 1, "{\"merchantId\":\"M-000002\",\"changes\":{}}"), Outbox("M-000001", 2), Outbox("M-000002", 2)]);
        var log = new InMemoryEventLog(1);
        var relay = new OutboxRelay(crm, log, SchemaRegistry.Default, _clock, new RelayOptions(), _telemetry);

        var report = await relay.RunOnceAsync(_ct);

        Assert.Equal(2, report.Published); // M-000001 #1 and #2
        Assert.Equal(1, report.Failed);    // M-000002 #1: "changes" may not be empty
        Assert.Equal(1, report.HeldBack);  // M-000002 #2 waits behind it
        var published = await log.ReadTopicAsync(Topics.CrmEvents, 10, _ct);
        Assert.Equal([1L, 2L], published.Select(m => CloudEvent.Parse(m.Value).Sequence));
        Assert.All(published, m => Assert.Equal("M-000001", m.Key));
        Assert.Equal(2, crm.Outbox.Count);
        Assert.Equal(1, crm.Outbox.Single(m => m.Sequence == 1).Attempts);
    }

    [Fact]
    public async Task Only_the_lease_holder_relays()
    {
        var crm = new FakeCrm();
        crm.Outbox.Add(Outbox("M-000001", 1));
        crm.Leases["outbox-relay"] = ("someone-else", DateTimeOffset.UtcNow.AddMinutes(1));
        var relay = new OutboxRelay(crm, new InMemoryEventLog(1), SchemaRegistry.Default, _clock, new RelayOptions { Holder = "me" }, _telemetry);
        Assert.False((await relay.RunOnceAsync(_ct)).LeaseHeld);
        Assert.Single(crm.Outbox);
    }

    private sealed class FakePlatform : IPlatformApi
    {
        public Dictionary<string, MerchantSnapshot> Snapshots { get; } = [];
        public Task<MerchantSnapshot?> GetSnapshotAsync(string merchantId, CancellationToken ct) => Task.FromResult(Snapshots.GetValueOrDefault(merchantId));
        public Task<IReadOnlyDictionary<string, MerchantSnapshot>> GetSnapshotsAsync(IReadOnlyCollection<string> merchantIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, MerchantSnapshot>>(merchantIds.Where(Snapshots.ContainsKey).ToDictionary(id => id, id => Snapshots[id]));
    }

    [Fact]
    public async Task The_reconciler_heals_missed_events_and_reports_hand_edits()
    {
        var crm = new FakeCrm();
        var platform = new FakePlatform();
        // M-1 missed the status event: the platform is ahead.
        crm.Merchants["M-000001"] = crm.Add("M-000001", LifecycleStage.Boarded) with { PlatformAccountId = "PA-1", KycDecision = "approved", KycVersion = 2 };
        platform.Snapshots["M-000001"] = new MerchantSnapshot("M-000001", "PA-1", new SnapshotKyc("approved", null, 2), new SnapshotStatus("live", null, 5), null);
        // M-2: same versions, different risk band: someone edited the CRM by hand.
        crm.Merchants["M-000002"] = crm.Add("M-000002", LifecycleStage.Live) with { PlatformStatus = "live", StatusVersion = 3, RiskBand = "low", RiskVersion = 4 };
        platform.Snapshots["M-000002"] = new MerchantSnapshot("M-000002", null, null, new SnapshotStatus("live", null, 3), new SnapshotRisk(0.02m, "high", 4));
        // M-3 is in sync.
        crm.Merchants["M-000003"] = crm.Add("M-000003", LifecycleStage.Live) with { PlatformStatus = "live", StatusVersion = 3 };
        platform.Snapshots["M-000003"] = new MerchantSnapshot("M-000003", null, null, new SnapshotStatus("live", null, 3), null);

        var applier = new PlatformEventApplier(crm, _clock, _telemetry);
        var reconciler = new Reconciler(crm, platform, applier, _clock, new ReconcilerOptions { PageSize = 2 }, _telemetry);
        var report = await reconciler.RunOnceAsync(_ct);

        Assert.Equal(3, report.Checked);
        Assert.Equal(1, report.Healed);
        Assert.Equal(1, report.NeedsReview);
        Assert.Equal(LifecycleStage.Live, crm.Merchants["M-000001"].Stage);
        Assert.Equal("low", crm.Merchants["M-000002"].RiskBand); // not overwritten silently
        Assert.Contains(crm.Drift, d => d.MerchantId == "M-000002" && d.Field == "risk band" && !d.Healed);
        Assert.Contains(crm.Drift, d => d.MerchantId == "M-000001" && d.Field == "status" && d.Healed);

        // Running again finds nothing new to heal.
        crm.Drift.Clear();
        var again = await reconciler.RunOnceAsync(_ct);
        Assert.Equal(0, again.Healed);
    }
}
