using System.Text.Json.Nodes;
using Bridge.Core.Contracts;
using Bridge.Core.Domain;
using Bridge.Core.Messaging;
using Bridge.Core.Observability;
using Bridge.Core.Ports;
using Bridge.Core.Services;
using Bridge.Dataverse;

namespace Bridge.Tests;

/// <summary>The services over the real Web API client and the emulator: what runs in production minus
/// Kafka and the tenant.</summary>
[Collection("emulator")]
public class GatewayTests(EmulatorFixture emulator) : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly BridgeTelemetry _telemetry = new();

    public async ValueTask InitializeAsync() => await emulator.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private DataverseCrmGateway Gateway() => new(emulator.Bridge(), TimeProvider.System);

    [Fact]
    public async Task Applying_platform_events_updates_the_account_and_the_ledger_in_one_changeset()
    {
        await emulator.OnboardAsync("M-000101");
        var gateway = Gateway();
        var applier = new PlatformEventApplier(gateway, TimeProvider.System, _telemetry);
        foreach (var ce in new[] { Events.Kyc("M-000101", 1, "approved"), Events.Boarded("M-000101", 2, "PA-77"), Events.Terminal("M-000101", 3, "T-1"), Events.Status("M-000101", 4, "live") })
            Assert.Equal(Outcome.Applied, (await applier.ApplyAsync(ce, EventReader.Default.Read(ce), _ct)).Outcome);

        var m = await gateway.GetMerchantAsync("M-000101", _ct);
        Assert.Equal(LifecycleStage.Live, m!.Stage);
        Assert.Equal("PA-77", m.PlatformAccountId);
        Assert.Equal(4, m.PlatformVersion);

        var bridge = emulator.Bridge();
        var ledger = await bridge.QueryAsync("mcb_inboundevents", "mcb_merchantid eq 'M-000101'", "mcb_eventtype,mcb_outcome", null, null, 50, _ct);
        Assert.Equal(4, ledger.Rows.Count);
        Assert.All(ledger.Rows, r => Assert.Equal(771000600, (int?)r["mcb_outcome"]));
        var terminal = await bridge.GetAsync("mcb_terminals", RowKey.Alternate(("mcb_terminalref", "T-1")), "mcb_merchantid", _ct);
        Assert.Equal("M-000101", (string?)terminal.Row!["mcb_merchantid"]);

        // The bridge's writes do not come back as CRM events (only onboarding is in the outbox).
        var outbox = await bridge.QueryAsync("mcb_outboxmessages", null, "mcb_eventtype", null, null, 50, _ct);
        Assert.Single(outbox.Rows);
    }

    [Fact]
    public async Task Duplicates_stale_and_rejected_events_each_leave_one_ledger_row()
    {
        await emulator.OnboardAsync("M-000102");
        var gateway = Gateway();
        var applier = new PlatformEventApplier(gateway, TimeProvider.System, _telemetry);
        var risk = Events.Risk("M-000102", 5, "high", 0.02m);
        Assert.Equal(Outcome.Applied, (await applier.ApplyAsync(risk, EventReader.Default.Read(risk), _ct)).Outcome);
        Assert.Equal(Outcome.Duplicate, (await applier.ApplyAsync(risk, EventReader.Default.Read(risk), _ct)).Outcome);
        var older = Events.Risk("M-000102", 4, "low", 0.001m);
        Assert.Equal(Outcome.Stale, (await applier.ApplyAsync(older, EventReader.Default.Read(older), _ct)).Outcome);
        var usd = Events.VolumeV2("M-000102", 6, "2026-09", 100, currency: "USD");
        Assert.Equal(Outcome.Rejected, (await applier.ApplyAsync(usd, EventReader.Default.Read(usd), _ct)).Outcome);

        var ledger = await emulator.Bridge().QueryAsync("mcb_inboundevents", "mcb_merchantid eq 'M-000102'", "mcb_outcome", "mcb_sequence asc", null, 50, _ct);
        Assert.Equal([771000601, 771000600, 771000602], ledger.Rows.Select(r => (int)r["mcb_outcome"]!));
        Assert.Equal("high", (await gateway.GetMerchantAsync("M-000102", _ct))!.RiskBand);
    }

    [Fact]
    public async Task A_person_editing_the_account_meanwhile_does_not_lose_either_change()
    {
        await emulator.OnboardAsync("M-000103");
        var gateway = Gateway();
        var read = (await gateway.GetMerchantAsync("M-000103", _ct))!;
        // Sales changes the pricing plan after the bridge read the row.
        await emulator.Person().PatchAsync("accounts", RowKey.Alternate(("mcb_merchantid", "M-000103")), new JsonObject { ["mcb_pricingplan"] = 771000202 }, "*", null, _ct);

        var ce = Events.Kyc("M-000103", 1, "approved");
        var effect = PlatformEventMapper.Decide(read, EventReader.Default.Read(ce), 1, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<ConcurrencyException>(() => gateway.CommitAsync(read, effect, new InboundRecord(ce.Id, ce.Type, "M-000103", 1, effect.Outcome, "", DateTimeOffset.UtcNow), _ct));

        var applier = new PlatformEventApplier(gateway, TimeProvider.System, _telemetry);
        var result = await applier.ApplyAsync(ce, EventReader.Default.Read(ce), _ct);
        Assert.Equal(Outcome.Applied, result.Outcome);
        var after = (await gateway.GetMerchantAsync("M-000103", _ct))!;
        Assert.Equal("approved", after.KycDecision);
        Assert.Equal("enterprise", after.PricingPlan);
    }

    [Fact]
    public async Task The_relay_publishes_the_dataverse_outbox_and_marks_rows_sent()
    {
        var onboardingId = await emulator.OnboardAsync("M-000104");
        await emulator.Person().PatchAsync("accounts", RowKey.Alternate(("mcb_merchantid", "M-000104")), new JsonObject { ["mcb_iban"] = "AT611904300234573201" }, "*", null, _ct);
        var gateway = Gateway();
        var log = new InMemoryEventLog(2);
        var relay = new OutboxRelay(gateway, log, SchemaRegistry.Default, TimeProvider.System, new RelayOptions { Holder = "test" }, _telemetry);

        var report = await relay.RunOnceAsync(_ct);
        Assert.Equal(2, report.Published);
        var events = (await log.ReadTopicAsync(Topics.CrmEvents, 10, _ct)).Select(m => CloudEvent.Parse(m.Value)).ToList();
        Assert.Equal(onboardingId, events[0].Id);
        Assert.Equal(EventTypes.OnboardingRequested, events[0].Type);
        Assert.Equal("AT611904300234573201", (string?)events[1].Data["changes"]!["iban"]);
        Assert.Empty(await gateway.GetPendingOutboxAsync(10, _ct));
        Assert.Equal(0, (await relay.RunOnceAsync(_ct)).Published);
    }

    [Fact]
    public async Task Leases_are_exclusive_until_they_expire()
    {
        var gateway = Gateway();
        Assert.True(await gateway.TryAcquireLeaseAsync("relay", "a", TimeSpan.FromMinutes(1), _ct));
        Assert.False(await gateway.TryAcquireLeaseAsync("relay", "b", TimeSpan.FromMinutes(1), _ct));
        Assert.True(await gateway.TryAcquireLeaseAsync("relay", "a", TimeSpan.FromMinutes(1), _ct)); // renew
        Assert.True(await gateway.TryAcquireLeaseAsync("other", "b", TimeSpan.FromSeconds(-1), _ct));
        Assert.True(await gateway.TryAcquireLeaseAsync("other", "c", TimeSpan.FromMinutes(1), _ct)); // expired: taken over
    }

    [Fact]
    public async Task Synced_merchants_are_listed_in_pages()
    {
        for (var i = 0; i < 5; i++) await emulator.OnboardAsync($"M-00020{i}");
        await emulator.Person().CreateAsync("accounts", EmulatorFixture.Prospect("M-000299"), _ct); // a prospect is not synced
        var gateway = Gateway();
        var ids = new List<string>();
        string? next = null;
        var pages = 0;
        do
        {
            var page = await gateway.ListSyncedMerchantsAsync(next, 2, _ct);
            ids.AddRange(page.Items.Select(m => m.MerchantId));
            next = page.Continuation;
            pages++;
        }
        while (next is not null);
        Assert.Equal(3, pages);
        Assert.Equal(["M-000200", "M-000201", "M-000202", "M-000203", "M-000204"], ids);
    }

    [Fact]
    public async Task Long_throttling_surfaces_as_a_transient_error_with_retry_after()
    {
        await emulator.OnboardAsync("M-000105");
        emulator.Options.ThrottleLimit = 1;
        emulator.Options.ThrottleWindow = TimeSpan.FromSeconds(60);
        var gateway = new DataverseCrmGateway(emulator.Bridge(options: new DataverseOptions { EnvironmentUrl = emulator.BaseUrl, MaxInlineRetryAfter = TimeSpan.FromSeconds(1) }), TimeProvider.System);
        await gateway.GetMerchantAsync("M-000105", _ct);
        var ex = await Assert.ThrowsAsync<TransientException>(() => gateway.GetMerchantAsync("M-000105", _ct));
        Assert.NotNull(ex.RetryAfter);
    }
}
