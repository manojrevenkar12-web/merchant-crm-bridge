using Bridge.Core.Domain;
using Bridge.Core.Ports;

namespace Bridge.Tests;

/// <summary>An in-memory CRM for unit tests of the services: applies patches the way the Dataverse
/// gateway would, with versions for optimistic concurrency and a unique ledger.</summary>
public sealed class FakeCrm : ICrmGateway
{
    private int _version;
    public Dictionary<string, CrmMerchant> Merchants { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, InboundRecord> Ledger { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, TerminalUpsert> Terminals { get; } = new(StringComparer.Ordinal);
    public List<OutboxMessage> Outbox { get; } = [];
    public List<DriftFinding> Drift { get; } = [];
    public Dictionary<string, (string Holder, DateTimeOffset Until)> Leases { get; } = [];
    public Func<CrmMerchant, CrmMerchant?>? BeforeCommit { get; set; }
    public int Commits { get; private set; }

    public CrmMerchant Add(string merchantId, LifecycleStage stage = LifecycleStage.Onboarding)
    {
        var m = new CrmMerchant { AccountId = Guid.NewGuid(), MerchantId = merchantId, ETag = Next(), Stage = stage };
        Merchants[merchantId] = m;
        return m;
    }

    private string Next() => $"W/\"{++_version}\"";

    public Task<CrmMerchant?> GetMerchantAsync(string merchantId, CancellationToken ct) => Task.FromResult(Merchants.GetValueOrDefault(merchantId));

    public Task<bool> InboundEventSeenAsync(string eventId, CancellationToken ct) => Task.FromResult(Ledger.ContainsKey(eventId));

    public Task CommitAsync(CrmMerchant read, Effect effect, InboundRecord ledger, CancellationToken ct)
    {
        // A concurrent writer, simulated: the test may change the row between read and write.
        if (BeforeCommit?.Invoke(Merchants[read.MerchantId]) is { } changed) Merchants[read.MerchantId] = changed with { ETag = Next() };
        if (effect.WritesMerchant && Merchants[read.MerchantId].ETag != read.ETag) throw new ConcurrencyException("etag");
        if (Ledger.ContainsKey(ledger.EventId)) throw new DuplicateEventException("ledger");
        if (effect.WritesMerchant) Merchants[read.MerchantId] = Apply(Merchants[read.MerchantId], effect.Patch) with { ETag = Next() };
        if (effect.Terminal is { } t) Terminals[t.TerminalId] = t;
        Ledger[ledger.EventId] = ledger;
        Commits++;
        return Task.CompletedTask;
    }

    public static CrmMerchant Apply(CrmMerchant m, MerchantPatch patch)
    {
        foreach (var (f, v) in patch.Values)
        {
            m = f switch
            {
                PlatformField.Stage => m with { Stage = (LifecycleStage)v! },
                PlatformField.PlatformAccountId => m with { PlatformAccountId = (string?)v },
                PlatformField.KycDecision => m with { KycDecision = (string?)v },
                PlatformField.KycReason => m with { KycReason = (string?)v },
                PlatformField.KycVersion => m with { KycVersion = (long)v! },
                PlatformField.PlatformStatus => m with { PlatformStatus = (string?)v },
                PlatformField.StatusReason => m with { StatusReason = (string?)v },
                PlatformField.StatusVersion => m with { StatusVersion = (long)v! },
                PlatformField.ChargebackRatio => m with { ChargebackRatio = (decimal?)v },
                PlatformField.RiskBand => m with { RiskBand = (string?)v },
                PlatformField.RiskVersion => m with { RiskVersion = (long)v! },
                PlatformField.VolumeMonth => m with { VolumeMonth = (string?)v },
                PlatformField.LastMonthVolume => m with { LastMonthVolume = (decimal?)v },
                PlatformField.LastMonthTransactions => m with { LastMonthTransactions = (int?)v },
                PlatformField.VolumeVersion => m with { VolumeVersion = (long)v! },
                PlatformField.PlatformVersion => m with { PlatformVersion = (long)v! },
                PlatformField.LastSyncedOn => m with { LastSyncedOn = (DateTimeOffset?)v },
                _ => m,
            };
        }
        return m;
    }

    public Task<IReadOnlyList<OutboxMessage>> GetPendingOutboxAsync(int max, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<OutboxMessage>>(Outbox.Take(max).ToList());

    public Task MarkOutboxSentAsync(OutboxMessage message, DateTimeOffset at, CancellationToken ct)
    {
        Outbox.RemoveAll(m => m.RowId == message.RowId);
        return Task.CompletedTask;
    }

    public Task MarkOutboxFailedAsync(OutboxMessage message, string error, CancellationToken ct)
    {
        var i = Outbox.FindIndex(m => m.RowId == message.RowId);
        if (i >= 0) Outbox[i] = Outbox[i] with { Attempts = Outbox[i].Attempts + 1 };
        return Task.CompletedTask;
    }

    public Task<CrmPage<CrmMerchant>> ListSyncedMerchantsAsync(string? continuation, int pageSize, CancellationToken ct)
    {
        var skip = continuation is null ? 0 : int.Parse(continuation, System.Globalization.CultureInfo.InvariantCulture);
        var all = Merchants.Values.Where(m => m.Stage != LifecycleStage.Prospect).OrderBy(m => m.MerchantId, StringComparer.Ordinal).ToList();
        var page = all.Skip(skip).Take(pageSize).ToList();
        var next = skip + page.Count < all.Count ? (skip + page.Count).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return Task.FromResult(new CrmPage<CrmMerchant>(page, next));
    }

    public Task WriteDriftAsync(DriftFinding finding, CancellationToken ct)
    {
        Drift.Add(finding);
        return Task.CompletedTask;
    }

    public Task<bool> TryAcquireLeaseAsync(string name, string holder, TimeSpan duration, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (Leases.TryGetValue(name, out var lease) && lease.Holder != holder && lease.Until > now) return Task.FromResult(false);
        Leases[name] = (holder, now + duration);
        return Task.FromResult(true);
    }
}
