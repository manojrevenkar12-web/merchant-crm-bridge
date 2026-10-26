using System.Diagnostics;
using Bridge.Core.Contracts;
using Bridge.Core.Domain;
using Bridge.Core.Observability;
using Bridge.Core.Ports;

namespace Bridge.Core.Services;

/// <summary>
/// Applies one platform event to the CRM exactly once in effect. Delivery is at least once, so: the
/// ledger row for the event id commits in the same $batch changeset as the merchant change (a second
/// delivery finds it); the merchant row is written with If-Match on the version that was read (a
/// concurrent writer forces a re-read and a new decision); stale and rejected events are recorded too,
/// so every delivered event leaves exactly one ledger row.
/// </summary>
public sealed class PlatformEventApplier(ICrmGateway crm, TimeProvider clock, BridgeTelemetry telemetry, int maxConcurrencyRetries = 5)
{
    public async Task<ApplyResult> ApplyAsync(CloudEvent ce, PlatformEvent evt, CancellationToken ct)
    {
        var started = clock.GetTimestamp();
        if (await crm.InboundEventSeenAsync(ce.Id, ct).ConfigureAwait(false))
            return Done(ce, new ApplyResult(Outcome.Duplicate, "already applied", 0), started);

        for (var attempt = 1; attempt <= maxConcurrencyRetries; attempt++)
        {
            var merchant = await crm.GetMerchantAsync(evt.MerchantId, ct).ConfigureAwait(false)
                ?? throw new UnknownMerchantException($"merchant {evt.MerchantId} is not in the CRM");
            var effect = PlatformEventMapper.Decide(merchant, evt, ce.Sequence, clock.GetUtcNow());
            var ledger = new InboundRecord(ce.Id, ce.Type, evt.MerchantId, ce.Sequence, effect.Outcome, Trim(effect.Detail), clock.GetUtcNow());
            try
            {
                await crm.CommitAsync(merchant, effect, ledger, ct).ConfigureAwait(false);
                return Done(ce, new ApplyResult(effect.Outcome, effect.Detail, attempt - 1), started);
            }
            catch (ConcurrencyException)
            {
                telemetry.ConcurrencyRetries.Add(1);
                Activity.Current?.AddEvent(new ActivityEvent("concurrency-retry"));
            }
            catch (DuplicateEventException)
            {
                return Done(ce, new ApplyResult(Outcome.Duplicate, "applied by another delivery", attempt - 1), started);
            }
        }
        throw new TransientException($"merchant {evt.MerchantId} kept changing underneath ({maxConcurrencyRetries} attempts)");
    }

    private ApplyResult Done(CloudEvent ce, ApplyResult result, long started)
    {
        telemetry.Consumed.Add(1, new KeyValuePair<string, object?>("type", ce.Type), new KeyValuePair<string, object?>("outcome", result.Outcome.ToString()));
        telemetry.ApplyDuration.Record(clock.GetElapsedTime(started).TotalMilliseconds);
        return result;
    }

    private static string Trim(string s) => s.Length <= 400 ? s : s[..397] + "...";
}

public sealed record ApplyResult(Outcome Outcome, string Detail, int ConcurrencyRetries);
