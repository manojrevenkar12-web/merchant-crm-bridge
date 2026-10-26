using System.Diagnostics;
using System.Text.Json.Nodes;
using Bridge.Core.Contracts;
using Bridge.Core.Domain;
using Bridge.Core.Messaging;
using Bridge.Core.Observability;
using Bridge.Core.Ports;
using Bridge.Core.Services;
using Bridge.Dataverse;
using Bridge.Emulator;
using Bridge.Kafka;
using Bridge.Platform;
using Bridge.PlatformSim;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Bridge.Drills;

public sealed record DrillConfig
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public int Merchants { get; init; } = 40;
    public int Seed { get; init; } = 7;
    public ChaosOptions Chaos { get; init; } = ChaosOptions.None;
    public int Consumers { get; init; } = 2;

    /// <summary>Chance that a consumer "crashes" after writing to the CRM but before committing the offset.</summary>
    public double CrashAfterApply { get; init; }

    public int ThrottleLimit { get; init; } = 6000;
    public int ThrottleWindowSeconds { get; init; } = 300;
    public int MaxLatencyMs { get; init; }
    public int FailBatchesEveryTicks { get; init; }
    public int EditsPerTick { get; init; } = 1;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(6);
}

public sealed record Check(string Name, bool Passed, string Detail);

public sealed record DrillResult(string Name, string Description, bool Passed, IReadOnlyList<Check> Checks, IReadOnlyDictionary<string, double> Numbers, double Seconds);

/// <summary>
/// One drill: the bridge (consumers, outbox relay, reconciler) runs against the Dataverse emulator and the
/// platform simulator, while a sales team edits the same rows and the configured faults happen. When
/// everything is quiet, the CRM and the platform are compared field by field.
/// </summary>
public sealed class Drill(DrillConfig config, string? kafkaBootstrap)
{
    private readonly Random _random = new(config.Seed);
    private int _crashes;
    private long _concurrencyRetries;
    private long _throttledWaits;

    public async Task<DrillResult> RunAsync(CancellationToken outer)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(outer);
        timeout.CancelAfter(config.Timeout);
        var ct = timeout.Token;
        var watch = Stopwatch.StartNew();
        using var listener = Listen();
        using var telemetry = new BridgeTelemetry();

        // --- the world ---------------------------------------------------------------------------
        var emulatorOptions = new EmulatorOptions
        {
            ThrottleLimit = config.ThrottleLimit,
            ThrottleWindow = TimeSpan.FromSeconds(config.ThrottleWindowSeconds),
            MaxLatency = TimeSpan.FromMilliseconds(config.MaxLatencyMs),
        };
        await using var emulator = EmulatorHost.Build(["--urls", "http://127.0.0.1:0"], emulatorOptions);
        await emulator.StartAsync(ct);
        var emulatorUrl = new Uri(Address(emulator) + "/");
        emulatorOptions.BaseUrl = new Uri(emulatorUrl, "api/data/v9.2/").ToString();

        IEventLog log;
        KafkaEventLog? kafka = null;
        if (kafkaBootstrap is null)
        {
            log = new InMemoryEventLog(partitions: 4);
        }
        else
        {
            kafka = new KafkaEventLog(new KafkaOptions { BootstrapServers = kafkaBootstrap, Partitions = 4 });
            await kafka.RecreateTopicsAsync([Topics.PlatformEvents, Topics.CrmEvents, Topics.PlatformEventsDlq], ct); // each drill starts from empty topics
            log = kafka;
        }

        var simulator = new Simulator(log, config.Chaos, config.Seed, TimeProvider.System);
        await using var platformHost = PlatformHost.Build(["--urls", "http://127.0.0.1:0"], log, simulator);
        await platformHost.StartAsync(ct);
        var platformUrl = new Uri(Address(platformHost) + "/graphql");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var dataverse = new DataverseClient(http, new StaticTokenProvider("emu.app.merchant-crm-bridge"),
            new DataverseOptions { EnvironmentUrl = emulatorUrl, MaxInlineRetryAfter = TimeSpan.FromSeconds(1) }, TimeProvider.System);
        var gateway = new DataverseCrmGateway(dataverse, TimeProvider.System);
        var people = Enumerable.Range(1, 3).Select(i => new DataverseClient(http, new StaticTokenProvider($"emu.user.{Guid.NewGuid()}.sales{i}"),
            new DataverseOptions { EnvironmentUrl = emulatorUrl }, TimeProvider.System)).ToList();
        var sales = new SalesTeam(people, _random);
        var groupId = "bridge-" + config.Name;

        // --- the bridge --------------------------------------------------------------------------
        var applier = new PlatformEventApplier(gateway, TimeProvider.System, telemetry);
        var consumer = new PlatformConsumer(log, applier, EventReader.Default, new ConsumerOptions { GroupId = groupId, MaxBackoff = TimeSpan.FromSeconds(2) }, telemetry);
        var relay = new OutboxRelay(gateway, log, SchemaRegistry.Default, TimeProvider.System, new RelayOptions { Holder = "drill" }, telemetry);
        var platformApi = new PlatformGraphQlClient(http, new StaticTokenProvider("emu.app.merchant-crm-bridge"), new PlatformOptions { GraphQlEndpoint = platformUrl });
        var reconciler = new Reconciler(gateway, platformApi, applier, TimeProvider.System, new ReconcilerOptions { Holder = "drill" }, telemetry);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workers = new List<Task>();
        for (var i = 0; i < config.Consumers; i++) workers.Add(ConsumeAsync(log, consumer, groupId, $"c{i}", stop.Token));
        workers.Add(Loop(() => relay.RunOnceAsync(stop.Token), TimeSpan.FromMilliseconds(150), stop.Token));

        await using var platformCrm = log.Subscribe("payments-platform-" + config.Name, [Topics.CrmEvents], "platform");
        var pump = new CrmEventPump(log, simulator);

        // --- activity ----------------------------------------------------------------------------
        var tick = 0;
        var toOnboard = config.Merchants;
        while (!ct.IsCancellationRequested)
        {
            tick++;
            if (toOnboard > 0)
            {
                var n = Math.Min(4, toOnboard);
                await sales.OnboardAsync(n, ct);
                toOnboard -= n;
            }
            if (tick % 3 == 0) await sales.EditAsync(config.EditsPerTick, ct);
            if (config.FailBatchesEveryTicks > 0 && tick % config.FailBatchesEveryTicks == 0) emulatorOptions.FailNextBatches = 2;
            await pump.DrainAsync(platformCrm, ct);
            await simulator.TickAsync(ct);
            await Task.Delay(15, ct);
            if (toOnboard == 0 && simulator.Idle && tick > 20) break;
        }

        // --- quiet down: everything sent, delivered and applied ----------------------------------
        await QuietAsync(log, gateway, pump, platformCrm, groupId, ct);
        var streamCheck = await StreamCheckAsync(dataverse, simulator, ct);
        var reconcile = await reconciler.RunOnceAsync(ct);
        await QuietAsync(log, gateway, pump, platformCrm, groupId, ct);
        await stop.CancelAsync();
        try { await Task.WhenAll(workers); } catch (OperationCanceledException) { }

        // --- checks ------------------------------------------------------------------------------
        var checks = await CheckAsync(dataverse, log, simulator, sales, ct);
        checks.Insert(0, streamCheck);
        var stats = JsonNode.Parse(await http.GetStringAsync(new Uri(emulatorUrl, "_emulator/stats"), ct))!;
        var numbers = new Dictionary<string, double>
        {
            ["merchants"] = sales.Onboarded.Count,
            ["platform_events_published"] = simulator.Published,
            ["duplicated_by_platform"] = simulator.Duplicated,
            ["reordered_by_platform"] = simulator.Reordered,
            ["dropped_by_platform"] = simulator.DroppedIds.Count,
            ["broken_messages"] = simulator.PoisonIds.Count,
            ["sales_edits"] = sales.Edits,
            ["consumer_crashes"] = _crashes,
            ["concurrency_retries"] = Interlocked.Read(ref _concurrencyRetries),
            ["throttled_responses"] = (double)stats["throttled"]!,
            ["throttled_waits"] = Interlocked.Read(ref _throttledWaits),
            ["dataverse_requests"] = (double)stats["served"]!,
            ["healed_by_reconciler"] = reconcile.Healed,
            ["needs_review"] = reconcile.NeedsReview,
        };
        kafka?.Dispose();
        await platformHost.StopAsync(CancellationToken.None);
        await emulator.StopAsync(CancellationToken.None);
        return new DrillResult(config.Name, config.Description, checks.All(c => c.Passed), checks, numbers, Math.Round(watch.Elapsed.TotalSeconds, 1));
    }

    private System.Diagnostics.Metrics.MeterListener Listen()
    {
        var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == BridgeTelemetry.Name && instrument.Name is "bridge.concurrency_retries" or "bridge.throttled") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "bridge.concurrency_retries") Interlocked.Add(ref _concurrencyRetries, value);
            else Interlocked.Add(ref _throttledWaits, value);
        });
        listener.Start();
        return listener;
    }

    private static string Address(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    private static async Task Loop(Func<Task> action, TimeSpan every, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await action(); }
            catch (OperationCanceledException) { return; }
            catch (TransientException) { /* throttled: next round */ }
            try { await Task.Delay(every, ct); } catch (OperationCanceledException) { return; }
        }
    }

    // A consumer that may "crash" between the CRM write and the offset commit: it leaves the group
    // without committing, and a new one starts; the message is delivered again to whoever owns it next.
    private async Task ConsumeAsync(IEventLog log, PlatformConsumer handler, string groupId, string name, CancellationToken ct)
    {
        var generation = 0;
        while (!ct.IsCancellationRequested)
        {
            var inner = log.Subscribe(groupId, [Topics.PlatformEvents], $"{name}.{generation++}");
            var consumer = new CrashingConsumer(inner, config.CrashAfterApply, _random);
            try
            {
                while (!ct.IsCancellationRequested && !consumer.Crashed)
                {
                    var message = await consumer.ConsumeAsync(TimeSpan.FromMilliseconds(200), ct);
                    if (message is null) continue;
                    var handled = await handler.HandleAsync(consumer, message, ct);
                    if (handled.Delay > TimeSpan.Zero) await Task.Delay(handled.Delay, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // stopping
            }
            if (consumer.Crashed) Interlocked.Increment(ref _crashes);
            await inner.DisposeAsync();
        }
    }

    private sealed class CrashingConsumer(IEventConsumer inner, double crashRate, Random random) : IEventConsumer
    {
        public bool Crashed { get; private set; }

        public Task<LogMessage?> ConsumeAsync(TimeSpan wait, CancellationToken ct) => inner.ConsumeAsync(wait, ct);

        public void Commit(LogMessage message)
        {
            bool crash;
            lock (random) crash = random.NextDouble() < crashRate;
            if (crash) { Crashed = true; return; } // the CRM has the change; the offset is lost
            inner.Commit(message);
        }

        public void Rewind(LogMessage message) => inner.Rewind(message);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private async Task QuietAsync(IEventLog log, DataverseCrmGateway gateway, CrmEventPump pump, IEventConsumer platformCrm, string groupId, CancellationToken ct)
    {
        var quiet = 0;
        while (quiet < 3)
        {
            ct.ThrowIfCancellationRequested();
            await pump.DrainAsync(platformCrm, ct);
            var lag = (await log.GetLagAsync(groupId, Topics.PlatformEvents, ct)).Values.Sum();
            int pending;
            try { pending = (await gateway.GetPendingOutboxAsync(10, ct)).Count; }
            catch (TransientException) { pending = 1; }
            quiet = lag == 0 && pending == 0 ? quiet + 1 : 0;
            await Task.Delay(400, ct);
        }
    }

    /// <summary>Before the reconciler runs: the event stream alone must have brought the CRM to the
    /// platform's state, except where the platform lost the latest event of an aspect.</summary>
    private static async Task<Check> StreamCheckAsync(DataverseClient crm, Simulator sim, CancellationToken ct)
    {
        var accounts = (await All(crm, "accounts", "mcb_merchantid ne null", DataverseCrmGateway.AccountColumns, ct))
            .ToDictionary(r => (string)r["mcb_merchantid"]!, r => DataverseCrmGateway.ToMerchant(r, ""), StringComparer.Ordinal);
        var unexplained = new List<string>();
        var excused = 0;
        foreach (var m in sim.Merchants.Values)
        {
            if (!accounts.TryGetValue(m.Id, out var a)) { unexplained.Add($"{m.Id} missing"); continue; }
            void Aspect(string type, object? crmValue, object? truth, long? truthVersion)
            {
                if (Equals(crmValue, truth)) return;
                if (truthVersion is { } v && sim.Dropped.Contains((m.Id, type, v))) { excused++; return; }
                unexplained.Add($"{m.Id} {type.Split('.').Last()}: {crmValue ?? "-"} vs {truth ?? "-"}");
            }
            Aspect(EventTypes.KycDecided, a.KycDecision, m.Kyc?.Decision, m.Kyc?.Version);
            Aspect(EventTypes.StatusChanged, a.PlatformStatus, m.Status?.Status, m.Status?.Version);
            Aspect(EventTypes.RiskChanged, a.RiskBand, m.Risk?.RiskBand, m.Risk?.Version);
            Aspect(EventTypes.RiskChanged, a.ChargebackRatio, m.Risk?.ChargebackRatio, m.Risk?.Version);
            Aspect(EventTypes.Boarded, a.PlatformAccountId, m.PlatformAccountId, null);
            Aspect(EventTypes.VolumeReported, a.LastMonthVolume, m.Volume?.Net, null);
        }
        return new Check("Events alone converge (before reconciling)", unexplained.Count == 0,
            unexplained.Count == 0 ? $"every difference explained by a lost event ({excused})" : string.Join("; ", unexplained.Take(8)));
    }

    private static async Task<List<Check>> CheckAsync(DataverseClient crm, IEventLog log, Simulator sim, SalesTeam sales, CancellationToken ct)
    {
        var checks = new List<Check>();
        var accounts = new Dictionary<string, CrmMerchant>(StringComparer.Ordinal);
        foreach (var row in await All(crm, "accounts", "mcb_merchantid ne null", DataverseCrmGateway.AccountColumns, ct))
            accounts[(string)row["mcb_merchantid"]!] = DataverseCrmGateway.ToMerchant(row, (string)row["@odata.etag"]!);

        // 1. Every merchant the platform knows looks the same in the CRM.
        var diffs = new List<string>();
        foreach (var m in sim.Merchants.Values)
        {
            if (!accounts.TryGetValue(m.Id, out var a)) { diffs.Add($"{m.Id} missing in CRM"); continue; }
            void Same(string field, object? crm, object? platform) { if (!Equals(crm, platform)) diffs.Add($"{m.Id} {field}: CRM {crm ?? "-"} vs platform {platform ?? "-"}"); }
            Same("kyc", a.KycDecision, m.Kyc?.Decision);
            Same("status", a.PlatformStatus, m.Status?.Status);
            Same("risk band", a.RiskBand, m.Risk?.RiskBand);
            Same("chargeback ratio", a.ChargebackRatio, m.Risk?.ChargebackRatio);
            Same("platform account", a.PlatformAccountId, m.PlatformAccountId);
            Same("volume month", a.VolumeMonth, m.Volume?.Month);
            Same("volume", a.LastMonthVolume, m.Volume?.Net);
            var truth = new CrmMerchant
            {
                AccountId = a.AccountId, MerchantId = m.Id, ETag = "", Stage = LifecycleStage.Onboarding,
                KycDecision = m.Kyc?.Decision, PlatformStatus = m.Status?.Status, PlatformAccountId = m.PlatformAccountId,
            };
            Same("stage", a.Stage, PlatformEventMapper.DeriveStage(truth));
        }
        checks.Add(new Check("CRM matches the platform", diffs.Count == 0 && sim.Merchants.Count > 0,
            diffs.Count == 0 ? $"{sim.Merchants.Count} merchants, every platform-owned column equal" : string.Join("; ", diffs.Take(8))));

        // 2. Terminals: exactly the platform's terminals, once each.
        var terminals = await All(crm, "mcb_terminals", null, "mcb_terminalref,mcb_merchantid", ct);
        var crmTerminals = terminals.Select(t => $"{t["mcb_merchantid"]}/{t["mcb_terminalref"]}").OrderBy(x => x, StringComparer.Ordinal).ToList();
        var platformTerminals = sim.Merchants.Values.SelectMany(m => m.Terminals.Select(t => $"{m.Id}/{t}")).OrderBy(x => x, StringComparer.Ordinal).ToList();
        checks.Add(new Check("Terminals once each", crmTerminals.SequenceEqual(platformTerminals, StringComparer.Ordinal), $"{crmTerminals.Count} in CRM, {platformTerminals.Count} on the platform"));

        // 3. Every event the platform published left exactly one ledger row (duplicates add none).
        var ledger = (await All(crm, "mcb_inboundevents", null, "mcb_eventid", ct)).Select(r => (string)r["mcb_eventid"]!).ToList();
        var missing = sim.PublishedIds.Where(id => !ledger.Contains(id)).ToList();
        var ledgerUnique = ledger.Distinct(StringComparer.Ordinal).Count() == ledger.Count;
        checks.Add(new Check("Each event recorded once", missing.Count == 0 && ledgerUnique,
            $"{sim.PublishedIds.Count} published, {ledger.Count} ledger rows (incl. reconciler snapshots), {missing.Count} missing"));

        // 4. The dead-letter topic holds exactly the broken messages.
        var dlq = await log.ReadTopicAsync(Topics.PlatformEventsDlq, 10_000, ct);
        var dlqIds = dlq.Select(m => { try { return CloudEvent.Parse(m.Value).Id; } catch (ContractException) { return "unparseable"; } }).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var poison = sim.PoisonIds.OrderBy(x => x, StringComparer.Ordinal).ToList();
        checks.Add(new Check("Dead letters are exactly the broken messages", dlqIds.SequenceEqual(poison, StringComparer.Ordinal),
            $"{poison.Count} broken, {dlqIds.Count} dead-lettered ({string.Join(", ", dlq.Select(m => m.Headers.GetValueOrDefault("dlq-reason")).Distinct())})"));

        // 5. Every CRM change reached the platform, and people's values were never overwritten.
        var crmDiffs = new List<string>();
        foreach (var (id, (plan, iban)) in sales.Expected)
        {
            var a = accounts[id];
            var planName = Choices.Name(Choices.PricingPlan, plan);
            if (a.PricingPlan != planName || a.Iban != iban) crmDiffs.Add($"{id} CRM lost a person's change");
            if (sim.Merchants.TryGetValue(id, out var p) && a.Stage is not (LifecycleStage.Rejected or LifecycleStage.Terminated) && (p.PricingPlan != planName || p.Iban != iban))
                crmDiffs.Add($"{id} platform has {p.PricingPlan}/{p.Iban}, CRM {planName}/{iban}");
        }
        var pending = await All(crm, "mcb_outboxmessages", $"mcb_status eq {Choices.OutboxStatus["pending"]}", "mcb_eventid", ct);
        checks.Add(new Check("CRM changes reached the platform", crmDiffs.Count == 0 && pending.Count == 0 && sales.Onboarded.All(sim.Merchants.ContainsKey),
            crmDiffs.Count == 0 ? $"{sales.Onboarded.Count} onboardings and {sales.Edits} edits delivered, outbox empty" : string.Join("; ", crmDiffs.Take(8))));

        // 6. No echo: every CRM event came from a person (onboarding or an edit), none from the bridge.
        var outbox = await All(crm, "mcb_outboxmessages", null, "mcb_eventtype,_createdby_value", ct);
        var onboardings = outbox.Count(r => (string?)r["mcb_eventtype"] == EventTypes.OnboardingRequested);
        var changes = outbox.Count - onboardings;
        checks.Add(new Check("No echo of the bridge's own writes", onboardings == sales.Onboarded.Count && changes <= sales.Edits,
            $"{onboardings} onboardings, {changes} detail changes for {sales.Edits} edits by people"));
        return checks;
    }

    private static async Task<List<JsonObject>> All(DataverseClient crm, string set, string? filter, string select, CancellationToken ct)
    {
        var rows = new List<JsonObject>();
        var page = await crm.QueryAsync(set, filter, select, null, null, 500, ct);
        rows.AddRange(page.Rows);
        while (page.NextLink is not null)
        {
            page = await crm.QueryUrlAsync(page.NextLink, 500, ct);
            rows.AddRange(page.Rows);
        }
        return rows;
    }
}
