using System.Globalization;
using System.Text.Json.Nodes;
using Bridge.Core.Contracts;
using Bridge.Core.Domain;
using Bridge.Core.Ports;
using Bridge.Dataverse;

namespace Bridge.Api;

public sealed record StageCount(string Stage, int Count);

public sealed record OutcomeCount(string Outcome, int Count);

public sealed record MinuteCount(DateTimeOffset Minute, int Applied, int Stale, int Rejected);

public sealed record Overview(
    int Merchants,
    IReadOnlyList<StageCount> Stages,
    int OutboxPending,
    IReadOnlyList<OutcomeCount> RecentOutcomes,
    IReadOnlyList<MinuteCount> Throughput,
    int DeadLetters,
    long ConsumerLag,
    int DriftNeedsReview,
    DateTimeOffset? LastReconcileAt,
    int? LastReconcileHealed,
    DateTimeOffset? LastRelayAt);

public sealed record MerchantSummary(string MerchantId, string? LegalName, string Stage, string? PricingPlan, string? RiskBand, string? PlatformStatus, DateTimeOffset? LastSyncedOn, long CrmVersion, long PlatformVersion);

public sealed record AspectView(string Aspect, string? Crm, string? Platform, long CrmVersion, long PlatformVersion, bool InSync);

public sealed record TimelineEntry(DateTimeOffset At, string Direction, string EventType, long Sequence, string Status, string? Detail, string EventId);

public sealed record MerchantDetail(MerchantSummary Summary, string? ContactEmail, string? LegalForm, string? Country, string? IbanMasked, string? Mcc,
    string? PlatformAccountId, string? KycDecision, string? KycReason, decimal? ChargebackRatio, string? VolumeMonth, decimal? LastMonthVolume,
    int? LastMonthTransactions, IReadOnlyList<string> Terminals, IReadOnlyList<AspectView> Aspects, bool PlatformReachable, IReadOnlyList<TimelineEntry> Timeline);

public sealed record DeadLetter(int Partition, long Offset, string MerchantId, string? EventId, string? EventType, string Reason, string Detail, string Source, int Deliveries, bool Replayed);

public sealed record DriftItem(string MerchantId, string Field, string? CrmValue, string? PlatformValue, string Resolution, DateTimeOffset DetectedOn);

/// <summary>Read models for operations, from Dataverse and Kafka. Sized for operations views (hundreds of
/// rows); a production rollup over millions of rows would use FetchXML aggregates instead.</summary>
public sealed class OpsQueries(DataverseClient crm, ICrmGateway gateway, IEventLog log, IPlatformApi platform, BridgeSettings settings, WorkerState workers, TimeProvider clock)
{
    private readonly HashSet<(int, long)> _replayed = [];

    public async Task<Overview> OverviewAsync(CancellationToken ct)
    {
        var accounts = await AllAsync("accounts", "mcb_merchantid ne null", "mcb_lifecyclestage", null, 5000, ct);
        var stages = accounts.GroupBy(a => Choices.StageOf((int?)a["mcb_lifecyclestage"]))
            .OrderBy(g => g.Key).Select(g => new StageCount(Stage(g.Key), g.Count())).ToList();
        var pending = (await crm.QueryAsync("mcb_outboxmessages", $"mcb_status eq {Choices.OutboxStatus["pending"]}", "mcb_eventid", null, 500, 500, ct)).Rows.Count;
        var since = clock.GetUtcNow().AddMinutes(-30);
        var recent = await AllAsync("mcb_inboundevents", $"mcb_processedon ge {Iso(since)}", "mcb_outcome,mcb_processedon", "mcb_processedon desc", 5000, ct);
        var outcomes = recent.GroupBy(r => Choices.Name(Choices.InboundOutcome, (int?)r["mcb_outcome"]) ?? "unknown")
            .Select(g => new OutcomeCount(g.Key, g.Count())).OrderByDescending(o => o.Count).ToList();
        var minutes = Enumerable.Range(0, 30).Select(i => Truncate(since.AddMinutes(i + 1))).ToList();
        var byMinute = recent.GroupBy(r => Truncate(DateTimeOffset.Parse((string)r["mcb_processedon"]!, CultureInfo.InvariantCulture))).ToDictionary(g => g.Key, g => g.ToList());
        var throughput = minutes.Select(m => byMinute.TryGetValue(m, out var rows)
            ? new MinuteCount(m, Count(rows, "applied"), Count(rows, "stale"), Count(rows, "rejected"))
            : new MinuteCount(m, 0, 0, 0)).ToList();
        var dlq = await log.ReadTopicAsync(Topics.PlatformEventsDlq, 1000, ct);
        var lag = (await log.GetLagAsync(settings.Kafka.GroupId, Topics.PlatformEvents, ct)).Values.Sum();
        var drift = (await crm.QueryAsync("mcb_driftfindings", $"mcb_resolution eq {Choices.DriftResolution["needs_review"]}", "mcb_field", null, 500, 500, ct)).Rows.Count;
        return new Overview(accounts.Count, stages, pending, outcomes, throughput, dlq.Count, lag, drift,
            workers.LastReconcileAt, workers.LastReconcile?.Healed, workers.LastRelayAt);
    }

    private static int Count(List<JsonObject> rows, string outcome) => rows.Count(r => Choices.Name(Choices.InboundOutcome, (int?)r["mcb_outcome"]) == outcome);

    private static DateTimeOffset Truncate(DateTimeOffset t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, TimeSpan.Zero);

    public async Task<IReadOnlyList<MerchantSummary>> MerchantsAsync(string? search, string? stage, int top, CancellationToken ct)
    {
        var filters = new List<string> { "mcb_merchantid ne null" };
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().Replace("'", "''", StringComparison.Ordinal);
            filters.Add($"(contains(name,'{s}') or contains(mcb_merchantid,'{s}'))");
        }
        if (!string.IsNullOrWhiteSpace(stage))
        {
            var value = Enum.TryParse<LifecycleStage>(stage.Replace("_", "", StringComparison.Ordinal), ignoreCase: true, out var st) ? Choices.Stage[st]
                : throw new ArgumentException($"unknown stage '{stage}'", nameof(stage));
            filters.Add($"mcb_lifecyclestage eq {value}");
        }
        var rows = await AllAsync("accounts", string.Join(" and ", filters), DataverseCrmGateway.AccountColumns, "modifiedon desc", Math.Clamp(top, 1, 500), ct);
        return rows.Select(r => Summary(DataverseCrmGateway.ToMerchant(r, ""))).ToList();
    }

    public async Task<MerchantDetail?> MerchantAsync(string merchantId, CancellationToken ct)
    {
        var m = await gateway.GetMerchantAsync(merchantId, ct);
        if (m is null) return null;
        MerchantSnapshot? snap = null;
        var reachable = true;
        try { snap = await platform.GetSnapshotAsync(merchantId, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TransientException or InvalidOperationException or TaskCanceledException) { reachable = false; }

        var aspects = new List<AspectView>
        {
            new("KYC", m.KycDecision, snap?.Kyc?.Decision, m.KycVersion, snap?.Kyc?.Version ?? 0, !reachable || (m.KycDecision == snap?.Kyc?.Decision && m.KycVersion == (snap?.Kyc?.Version ?? 0))),
            new("Status", m.PlatformStatus, snap?.Status?.Status, m.StatusVersion, snap?.Status?.Version ?? 0, !reachable || (m.PlatformStatus == snap?.Status?.Status && m.StatusVersion == (snap?.Status?.Version ?? 0))),
            new("Risk", m.RiskBand, snap?.Risk?.RiskBand, m.RiskVersion, snap?.Risk?.Version ?? 0, !reachable || (m.RiskBand == snap?.Risk?.RiskBand && m.RiskVersion == (snap?.Risk?.Version ?? 0))),
            new("Platform account", m.PlatformAccountId, snap?.PlatformAccountId, 0, 0, !reachable || m.PlatformAccountId == snap?.PlatformAccountId),
        };

        var key = merchantId.Replace("'", "''", StringComparison.Ordinal);
        var terminals = await AllAsync("mcb_terminals", $"mcb_merchantid eq '{key}'", "mcb_terminalref", "mcb_terminalref asc", 200, ct);
        var outbox = await AllAsync("mcb_outboxmessages", $"mcb_merchantid eq '{key}'", "mcb_eventid,mcb_eventtype,mcb_sequence,mcb_status,mcb_lasterror,mcb_attempts,createdon,mcb_publishedon", null, 200, ct);
        var inbound = await AllAsync("mcb_inboundevents", $"mcb_merchantid eq '{key}'", "mcb_eventid,mcb_eventtype,mcb_sequence,mcb_outcome,mcb_detail,mcb_processedon", null, 500, ct);
        var timeline = outbox.Select(o => new TimelineEntry(
                DateTimeOffset.Parse((string)o["createdon"]!, CultureInfo.InvariantCulture), "crm-to-platform", (string)o["mcb_eventtype"]!, (long?)o["mcb_sequence"] ?? 0,
                Choices.Name(Choices.OutboxStatus, (int?)o["mcb_status"]) ?? "pending", (string?)o["mcb_lasterror"], (string)o["mcb_eventid"]!))
            .Concat(inbound.Select(i => new TimelineEntry(
                DateTimeOffset.Parse((string)i["mcb_processedon"]!, CultureInfo.InvariantCulture), "platform-to-crm", (string)i["mcb_eventtype"]!, (long?)i["mcb_sequence"] ?? 0,
                Choices.Name(Choices.InboundOutcome, (int?)i["mcb_outcome"]) ?? "unknown", (string?)i["mcb_detail"], (string)i["mcb_eventid"]!)))
            .OrderByDescending(t => t.At).ThenByDescending(t => t.Sequence).ToList();

        return new MerchantDetail(Summary(m), m.ContactEmail, m.LegalForm, m.Country, Mask(m.Iban), m.Mcc, m.PlatformAccountId, m.KycDecision, m.KycReason,
            m.ChargebackRatio, m.VolumeMonth, m.LastMonthVolume, m.LastMonthTransactions, terminals.Select(t => (string)t["mcb_terminalref"]!).ToList(), aspects, reachable, timeline);
    }

    public async Task<IReadOnlyList<DeadLetter>> DeadLettersAsync(CancellationToken ct)
    {
        var messages = await log.ReadTopicAsync(Topics.PlatformEventsDlq, 1000, ct);
        return messages.Select(m =>
        {
            string? id = null, type = null;
            try { var ce = CloudEvent.Parse(m.Value); id = ce.Id; type = ce.Type; } catch (ContractException) { }
            var source = m.Headers.GetValueOrDefault("dlq-source") ?? "";
            int.TryParse(m.Headers.GetValueOrDefault("dlq-deliveries"), CultureInfo.InvariantCulture, out var deliveries);
            bool replayed;
            lock (_replayed) replayed = _replayed.Contains((m.Partition, m.Offset));
            return new DeadLetter(m.Partition, m.Offset, m.Key, id, type, m.Headers.GetValueOrDefault("dlq-reason") ?? "unknown",
                m.Headers.GetValueOrDefault("dlq-detail") ?? "", source, deliveries, replayed);
        }).OrderByDescending(d => d.Offset).ToList();
    }

    /// <summary>Publishes a dead letter again to the platform topic, for after a fix (a new schema version, a
    /// merchant that now exists). Each dead letter is replayed at most once from here.</summary>
    public async Task<bool?> ReplayAsync(int partition, long offset, string by, CancellationToken ct)
    {
        var messages = await log.ReadTopicAsync(Topics.PlatformEventsDlq, 1000, ct);
        var m = messages.FirstOrDefault(x => x.Partition == partition && x.Offset == offset);
        if (m is null) return null;
        lock (_replayed) if (!_replayed.Add((partition, offset))) return false;
        var headers = new Dictionary<string, string>
        {
            ["replay-of"] = $"{Topics.PlatformEventsDlq}/{partition}/{offset}",
            ["replay-by"] = by,
        };
        if (m.Headers.TryGetValue("traceparent", out var tp)) headers["traceparent"] = tp;
        await log.ProduceAsync(Topics.PlatformEvents, m.Key, m.Value, headers, ct);
        return true;
    }

    public async Task<IReadOnlyList<DriftItem>> DriftAsync(CancellationToken ct)
    {
        var rows = await AllAsync("mcb_driftfindings", null, "mcb_merchantid,mcb_field,mcb_crmvalue,mcb_platformvalue,mcb_resolution,mcb_detectedon", "mcb_detectedon desc", 500, ct);
        return rows.Select(r => new DriftItem((string)r["mcb_merchantid"]!, (string)r["mcb_field"]!, (string?)r["mcb_crmvalue"], (string?)r["mcb_platformvalue"],
            Choices.Name(Choices.DriftResolution, (int?)r["mcb_resolution"]) ?? "needs_review", DateTimeOffset.Parse((string)r["mcb_detectedon"]!, CultureInfo.InvariantCulture)))
            .OrderBy(d => d.Resolution == "needs_review" ? 0 : 1).ThenByDescending(d => d.DetectedOn).ToList();
    }

    public static MerchantSummary Summary(CrmMerchant m) =>
        new(m.MerchantId, m.LegalName, Stage(m.Stage), m.PricingPlan, m.RiskBand, m.PlatformStatus, m.LastSyncedOn, m.CrmVersion, m.PlatformVersion);

    public static string Stage(LifecycleStage s) => s switch
    {
        LifecycleStage.KycPending => "kyc_pending",
        _ => s.ToString().ToLowerInvariant(),
    };

    /// <summary>Operations see where money goes, not the account: DE89 •••• 3000.</summary>
    public static string? Mask(string? iban) => iban is { Length: > 8 } ? $"{iban[..4]} •••• {iban[^4..]}" : iban;

    private static string Iso(DateTimeOffset d) => d.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private async Task<List<JsonObject>> AllAsync(string set, string? filter, string select, string? orderBy, int max, CancellationToken ct)
    {
        var rows = new List<JsonObject>();
        var page = await crm.QueryAsync(set, filter, select, orderBy, max, Math.Min(max, 500), ct);
        rows.AddRange(page.Rows);
        while (page.NextLink is not null && rows.Count < max)
        {
            page = await crm.QueryUrlAsync(page.NextLink, 500, ct);
            rows.AddRange(page.Rows);
        }
        return rows;
    }
}
