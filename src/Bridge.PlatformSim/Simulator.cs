using System.Globalization;
using System.Text.Json.Nodes;
using Bridge.Core.Contracts;
using Bridge.Core.Ports;

namespace Bridge.PlatformSim;

public sealed class ChaosOptions
{
    /// <summary>Share of events sent twice.</summary>
    public double Duplicate { get; set; } = 0.10;

    /// <summary>Share of events held back and sent after the merchant's next event.</summary>
    public double Reorder { get; set; } = 0.10;

    /// <summary>Share of state events never sent (the reconciler must find them).</summary>
    public double Drop { get; set; } = 0.02;

    /// <summary>Share of volume reports sent in the old v1 schema.</summary>
    public double OldVolumeSchema { get; set; } = 0.3;

    /// <summary>Broken messages per hundred ticks.</summary>
    public double PoisonPerHundredTicks { get; set; } = 2;

    public static ChaosOptions None => new() { Duplicate = 0, Reorder = 0, Drop = 0, OldVolumeSchema = 0, PoisonPerHundredTicks = 0 };
}

public sealed class SimMerchant
{
    public required string Id { get; init; }
    public string? LegalName { get; set; }
    public string? Mcc { get; set; }
    public string? Iban { get; set; }
    public string? PricingPlan { get; set; }
    public long CrmVersionApplied { get; set; }
    public long Sequence { get; set; }
    public string? PlatformAccountId { get; set; }
    public SnapshotKyc? Kyc { get; set; }
    public SnapshotStatus? Status { get; set; }
    public SnapshotRisk? Risk { get; set; }
    public List<string> Terminals { get; } = [];
    public Queue<string> Plan { get; } = new();

    /// <summary>The latest month reported and its net volume in euro (what the CRM should show).</summary>
    public (string Month, decimal Net)? Volume { get; set; }
    public CloudEvent? HeldBack { get; set; }

    public MerchantSnapshot Snapshot() => new(Id, PlatformAccountId, Kyc, Status, Risk);
}

/// <summary>
/// The platform: deterministic for a seed. Each tick advances some merchants by one step of their plan
/// and publishes the event, with the configured chaos. It remembers what it really did (the truth the
/// drills compare the CRM with) and which messages it broke on purpose (what must end in the DLQ).
/// </summary>
public sealed class Simulator(IEventLog log, ChaosOptions chaos, int seed, TimeProvider clock)
{
    private readonly Random _random = new(seed);
    private readonly object _gate = new();
    private readonly HashSet<string> _seenCrmEvents = new(StringComparer.Ordinal);
    private int _ticks;

    public Dictionary<string, SimMerchant> Merchants { get; } = new(StringComparer.Ordinal);
    public List<string> PoisonIds { get; } = [];
    public List<string> DroppedIds { get; } = [];

    /// <summary>The dropped events by merchant, type and sequence: only these may leave the CRM behind.</summary>
    public HashSet<(string Merchant, string Type, long Sequence)> Dropped { get; } = [];
    public int Published { get; private set; }
    public int Duplicated { get; private set; }
    public int Reordered { get; private set; }
    public int DuplicateCrmEvents { get; private set; }

    /// <summary>Every event id the platform meant to publish (not the broken ones, not the dropped ones).</summary>
    public HashSet<string> PublishedIds { get; } = new(StringComparer.Ordinal);

    /// <summary>Handles a CRM event once (the platform deduplicates by event id, as the contract requires).</summary>
    public void HandleCrmEvent(CloudEvent ce)
    {
        lock (_gate)
        {
            if (!_seenCrmEvents.Add(ce.Id)) { DuplicateCrmEvents++; return; }
            var data = ce.Data.AsObject();
            if (ce.Type == EventTypes.OnboardingRequested)
            {
                if (!Merchants.TryGetValue(ce.Subject, out var m))
                {
                    m = new SimMerchant { Id = ce.Subject };
                    Merchants[ce.Subject] = m;
                }
                if (ce.Sequence <= m.CrmVersionApplied) return;
                m.CrmVersionApplied = ce.Sequence;
                m.LegalName = (string?)data["legalName"];
                m.Mcc = (string?)data["mcc"];
                m.Iban = (string?)data["iban"];
                m.PricingPlan = (string?)data["pricingPlan"];
                m.Plan.Clear();
                foreach (var step in PlanFor(m)) m.Plan.Enqueue(step);
            }
            else if (ce.Type == EventTypes.DetailsChanged && Merchants.TryGetValue(ce.Subject, out var known))
            {
                if (ce.Sequence <= known.CrmVersionApplied) return; // older change arrived late
                known.CrmVersionApplied = ce.Sequence;
                var changes = data["changes"]!.AsObject();
                if (changes["iban"] is { } iban) known.Iban = (string?)iban;
                if (changes["mcc"] is { } mcc) known.Mcc = (string?)mcc;
                if (changes["legalName"] is { } name) known.LegalName = (string?)name;
                if (changes["pricingPlan"] is { } plan) known.PricingPlan = (string?)plan;
            }
        }
    }

    // A merchant's life on the platform. Gambling (MCC 7995) is rejected; risky categories need more
    // information first; most go live, take terminals, report volume, and some get restricted.
    private IEnumerable<string> PlanFor(SimMerchant m)
    {
        if (m.Mcc == "7995") { yield return "kyc:rejected"; yield break; }
        if (m.Mcc is "5993" or "5921" || _random.NextDouble() < 0.15) yield return "kyc:more_information";
        yield return "kyc:approved";
        yield return "boarded";
        var terminals = _random.Next(0, 3);
        for (var i = 0; i < terminals; i++) yield return "terminal";
        yield return "status:live";
        yield return "volume:2026-08";
        yield return "risk";
        yield return "volume:2026-09";
        if (_random.NextDouble() < 0.3) yield return "volume:2026-09"; // restatement
        if (_random.NextDouble() < 0.25) yield return "risk:high";
        if (_random.NextDouble() < 0.15) yield return "status:restricted";
        if (_random.NextDouble() < 0.05) yield return "status:terminated";
    }

    public bool Idle
    {
        get { lock (_gate) return Merchants.Values.All(m => m.Plan.Count == 0 && m.HeldBack is null); }
    }

    /// <summary>One step: some merchants advance; held-back events eventually go out.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var outgoing = new List<(CloudEvent Event, bool Duplicate)>();
        string? poison = null;
        lock (_gate)
        {
            _ticks++;
            foreach (var m in Merchants.Values.OrderBy(m => m.Id, StringComparer.Ordinal))
            {
                if (m.Plan.Count == 0)
                {
                    if (m.HeldBack is { } late) { outgoing.Add((late, false)); m.HeldBack = null; }
                    continue;
                }
                if (_random.NextDouble() < 0.5) continue;
                var ce = Step(m, m.Plan.Dequeue());
                if (ce is null) continue;
                var stateEvent = ce.Type is EventTypes.KycDecided or EventTypes.StatusChanged or EventTypes.RiskChanged;
                if (stateEvent && _random.NextDouble() < chaos.Drop) { DroppedIds.Add(ce.Id); Dropped.Add((m.Id, ce.Type, ce.Sequence)); continue; }
                if (m.HeldBack is null && m.Plan.Count > 0 && _random.NextDouble() < chaos.Reorder)
                {
                    m.HeldBack = ce; // goes out after the merchant's next event
                    PublishedIds.Add(ce.Id);
                    Reordered++;
                    continue;
                }
                PublishedIds.Add(ce.Id);
                outgoing.Add((ce, false));
                if (m.HeldBack is { } held) { outgoing.Add((held, false)); m.HeldBack = null; }
                if (_random.NextDouble() < chaos.Duplicate) { outgoing.Add((ce, true)); Duplicated++; }
            }
            if (_random.NextDouble() * 100 < chaos.PoisonPerHundredTicks && Merchants.Count > 0)
            {
                var victim = Merchants.Keys.ElementAt(_random.Next(Merchants.Count));
                poison = Guid.NewGuid().ToString();
                PoisonIds.Add(poison);
                outgoing.Add((Poison(victim, poison), false));
            }
        }
        foreach (var (ce, _) in outgoing)
        {
            await log.ProduceAsync(Topics.PlatformEvents, ce.Subject, ce.Serialize(), null, ct).ConfigureAwait(false);
            Published++;
        }
    }

    private CloudEvent Poison(string merchantId, string id)
    {
        // A risk event with a ratio above 1: valid JSON, invalid against the schema.
        return Event(merchantId, id, EventTypes.RiskChanged, 1, 1, new JsonObject { ["chargebackRatio"] = 3.7, ["riskBand"] = "high" });
    }

    private CloudEvent? Step(SimMerchant m, string step)
    {
        var parts = step.Split(':');
        var seq = ++m.Sequence;
        var id = Guid.NewGuid().ToString();
        switch (parts[0])
        {
            case "kyc":
                m.Kyc = new SnapshotKyc(parts[1], parts[1] == "approved" ? null : parts[1] == "rejected" ? "PROHIBITED_MCC" : "UBO_DOCUMENT_MISSING", seq);
                return Event(m.Id, id, EventTypes.KycDecided, seq, 1, new JsonObject { ["decision"] = parts[1], ["reasonCode"] = m.Kyc.ReasonCode });
            case "boarded":
                m.PlatformAccountId = "PA-" + m.Id[2..];
                return Event(m.Id, id, EventTypes.Boarded, seq, 1, new JsonObject { ["platformAccountId"] = m.PlatformAccountId });
            case "terminal":
                var terminal = $"T-{m.Id[2..]}-{m.Terminals.Count + 1}";
                m.Terminals.Add(terminal);
                return Event(m.Id, id, EventTypes.TerminalActivated, seq, 1, new JsonObject
                {
                    ["terminalId"] = terminal,
                    ["model"] = "Smart POS A920",
                    ["activatedAt"] = clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                });
            case "status":
                m.Status = new SnapshotStatus(parts[1], parts[1] == "live" ? null : "CHARGEBACK_MONITORING", seq);
                return Event(m.Id, id, EventTypes.StatusChanged, seq, 1, new JsonObject { ["status"] = parts[1], ["reason"] = m.Status.Reason });
            case "risk":
                var high = parts.Length > 1 && parts[1] == "high";
                var ratio = Math.Round(high ? 0.012m + (decimal)_random.NextDouble() / 100 : (decimal)_random.NextDouble() / 200, 4);
                m.Risk = new SnapshotRisk(ratio, high ? "high" : ratio > 0.003m ? "medium" : "low", seq);
                return Event(m.Id, id, EventTypes.RiskChanged, seq, 1, new JsonObject { ["chargebackRatio"] = ratio, ["riskBand"] = m.Risk.RiskBand });
            case "volume":
                var gross = (long)_random.Next(200_000, 9_000_000);
                var refunds = (long)_random.Next(0, 50_000);
                var old = _random.NextDouble() < chaos.OldVolumeSchema;
                if (m.Volume is null || string.CompareOrdinal(parts[1], m.Volume.Value.Month) >= 0)
                    m.Volume = (parts[1], (gross - (old ? 0 : refunds)) / 100m);
                return old
                    ? Event(m.Id, id, EventTypes.VolumeReported, seq, 1, new JsonObject { ["month"] = parts[1], ["volumeCents"] = gross, ["transactions"] = gross / 2500 })
                    : Event(m.Id, id, EventTypes.VolumeReported, seq, 2, new JsonObject
                    {
                        ["month"] = parts[1],
                        ["gross"] = new JsonObject { ["amountMinor"] = gross, ["currency"] = "EUR" },
                        ["transactions"] = gross / 2500,
                        ["refundsMinor"] = refunds,
                    });
            default:
                return null;
        }
    }

    private CloudEvent Event(string merchantId, string id, string type, long seq, int version, JsonObject data)
    {
        data["merchantId"] = merchantId;
        foreach (var key in data.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList()) data.Remove(key);
        return new CloudEvent
        {
            Id = id,
            Source = "payments-platform/merchant-service",
            Type = type,
            Subject = merchantId,
            Time = clock.GetUtcNow(),
            Sequence = seq,
            SchemaVersion = version,
            DataSchema = CloudEvent.SchemaUri(type, version),
            Data = data,
        };
    }

    /// <summary>The platform's current view of a merchant, as its API serves it.</summary>
    public MerchantSnapshot? Snapshot(string merchantId)
    {
        lock (_gate) return Merchants.TryGetValue(merchantId, out var m) ? m.Snapshot() : null;
    }

    public int Ticks => _ticks;
}
