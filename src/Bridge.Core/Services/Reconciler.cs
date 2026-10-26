using System.Globalization;
using System.Text.Json.Nodes;
using Bridge.Core.Contracts;
using Bridge.Core.Domain;
using Bridge.Core.Observability;
using Bridge.Core.Ports;

namespace Bridge.Core.Services;

public sealed class ReconcilerOptions
{
    public int PageSize { get; set; } = 50;
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(10);
    public string Holder { get; set; } = Environment.MachineName + "/" + Environment.ProcessId;
}

public sealed record ReconcileReport(int Checked, int Healed, int NeedsReview, int MissingOnPlatform, bool LeaseHeld);

/// <summary>
/// Compares every merchant the platform knows with the platform's own view (its GraphQL API). Where the
/// platform has a newer version of an aspect, the CRM missed an event: the snapshot is applied through
/// the same applier, so the same rules hold. Where versions match but values differ, someone changed a
/// platform-owned column in the CRM by hand: that is reported for review, never overwritten silently.
/// </summary>
public sealed class Reconciler(ICrmGateway crm, IPlatformApi platform, PlatformEventApplier applier, TimeProvider clock, ReconcilerOptions options, BridgeTelemetry telemetry)
{
    public async Task<ReconcileReport> RunOnceAsync(CancellationToken ct)
    {
        if (!await crm.TryAcquireLeaseAsync("reconciler", options.Holder, options.LeaseDuration, ct).ConfigureAwait(false))
            return new ReconcileReport(0, 0, 0, 0, false);

        int checkedCount = 0, healed = 0, review = 0, missing = 0;
        string? continuation = null;
        do
        {
            var page = await crm.ListSyncedMerchantsAsync(continuation, options.PageSize, ct).ConfigureAwait(false);
            continuation = page.Continuation;
            if (page.Items.Count == 0) break;
            var snapshots = await platform.GetSnapshotsAsync(page.Items.Select(m => m.MerchantId).ToList(), ct).ConfigureAwait(false);
            foreach (var merchant in page.Items)
            {
                checkedCount++;
                if (!snapshots.TryGetValue(merchant.MerchantId, out var snap))
                {
                    // Onboarding was requested but the platform has not created the merchant yet; only boarded ones must exist.
                    if (merchant.PlatformAccountId is not null)
                    {
                        missing++;
                        await Report(merchant, "platform account", merchant.PlatformAccountId, null, healed: false, ct).ConfigureAwait(false);
                    }
                    continue;
                }
                var (h, r) = await CompareAsync(merchant, snap, ct).ConfigureAwait(false);
                healed += h;
                review += r;
            }
        }
        while (continuation is not null);

        return new ReconcileReport(checkedCount, healed, review, missing, true);
    }

    private async Task<(int Healed, int Review)> CompareAsync(CrmMerchant m, MerchantSnapshot snap, CancellationToken ct)
    {
        var healed = new List<(string Field, string? Crm, string? Platform)>();
        var review = new List<(string Field, string? Crm, string? Platform)>();

        void Aspect(string name, long crmVersion, long platformVersion, string? crmValue, string? platformValue)
        {
            if (platformVersion > crmVersion) healed.Add((name, crmValue, platformValue));
            else if (platformVersion == crmVersion && crmValue != platformValue) review.Add((name, crmValue, platformValue));
            else if (platformVersion < crmVersion) review.Add((name + " version", crmVersion.ToString(CultureInfo.InvariantCulture), platformVersion.ToString(CultureInfo.InvariantCulture)));
        }

        if (snap.Kyc is { } k) Aspect("kyc", m.KycVersion, k.Version, m.KycDecision, k.Decision);
        if (snap.Status is { } s) Aspect("status", m.StatusVersion, s.Version, m.PlatformStatus, s.Status);
        if (snap.Risk is { } r) Aspect("risk band", m.RiskVersion, r.Version, m.RiskBand, r.RiskBand);
        if (snap.PlatformAccountId is not null && m.PlatformAccountId is null) healed.Add(("platform account", null, snap.PlatformAccountId));
        else if (snap.PlatformAccountId is not null && m.PlatformAccountId != snap.PlatformAccountId) review.Add(("platform account", m.PlatformAccountId, snap.PlatformAccountId));

        if (healed.Count > 0)
        {
            var version = new[] { snap.Kyc?.Version ?? 0, snap.Status?.Version ?? 0, snap.Risk?.Version ?? 0 }.Max();
            var ce = new CloudEvent
            {
                Id = $"snapshot:{m.MerchantId}:{version}:{(snap.PlatformAccountId is null ? 0 : 1)}",
                Source = "reconciler",
                Type = EventTypes.Snapshot,
                Subject = m.MerchantId,
                Time = clock.GetUtcNow(),
                Sequence = Math.Max(version, 1),
                Data = JsonNode.Parse("{}")!,
            };
            await applier.ApplyAsync(ce, snap, ct).ConfigureAwait(false);
            foreach (var (field, crmValue, platformValue) in healed)
                await Report(m, field, crmValue, platformValue, healed: true, ct).ConfigureAwait(false);
        }
        foreach (var (field, crmValue, platformValue) in review)
            await Report(m, field, crmValue, platformValue, healed: false, ct).ConfigureAwait(false);
        return (healed.Count, review.Count);
    }

    private async Task Report(CrmMerchant m, string field, string? crmValue, string? platformValue, bool healed, CancellationToken ct)
    {
        telemetry.Drift.Add(1, new KeyValuePair<string, object?>("resolution", healed ? "healed" : "needs_review"));
        await crm.WriteDriftAsync(new DriftFinding(m.MerchantId, field, crmValue, platformValue, healed, clock.GetUtcNow()), ct).ConfigureAwait(false);
    }
}
