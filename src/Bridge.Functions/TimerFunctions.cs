using Bridge.Core.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Bridge.Functions;

/// <summary>The outbox relay and the reconciler on timers. Timers run on one instance at a time, and both
/// also take a lease row in Dataverse, so a second host (the App Service with RunWorkers on) cannot
/// double-publish or double-heal.</summary>
public sealed class TimerFunctions(OutboxRelay relay, Reconciler reconciler, ILogger<TimerFunctions> logger)
{
    [Function("outbox-relay")]
    public async Task RelayAsync([TimerTrigger("%Bridge:RelaySchedule%")] TimerInfo timer, FunctionContext context)
    {
        var report = await relay.RunOnceAsync(context.CancellationToken);
        if (report.Published > 0 || report.Failed > 0)
            logger.LogInformation("outbox relay: {Published} published, {Failed} failed, {Held} held back", report.Published, report.Failed, report.HeldBack);
    }

    [Function("reconciler")]
    public async Task ReconcileAsync([TimerTrigger("%Bridge:ReconcileSchedule%")] TimerInfo timer, FunctionContext context)
    {
        var report = await reconciler.RunOnceAsync(context.CancellationToken);
        logger.LogInformation("reconciled {Checked} merchants: {Healed} healed, {NeedsReview} need review, {Missing} missing on the platform",
            report.Checked, report.Healed, report.NeedsReview, report.MissingOnPlatform);
    }
}
