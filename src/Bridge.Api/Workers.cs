using Bridge.Core.Contracts;
using Bridge.Core.Ports;
using Bridge.Core.Services;

namespace Bridge.Api;

/// <summary>What the workers last did, for the operations view.</summary>
public sealed class WorkerState
{
    public ReconcileReport? LastReconcile { get; set; }
    public DateTimeOffset? LastReconcileAt { get; set; }
    public RelayReport? LastRelay { get; set; }
    public DateTimeOffset? LastRelayAt { get; set; }
    public int Published { get; set; }
}

/// <summary>Consumers, the outbox relay and the reconciler as background loops (container hosting).</summary>
public sealed class Workers(BridgeSettings settings, IEventLog log, PlatformConsumer consumer, OutboxRelay relay, Reconciler reconciler,
    WorkerState state, TimeProvider clock, ILogger<Workers> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = new List<Task>();
        for (var i = 0; i < settings.Consumers; i++)
        {
            var name = $"{Environment.MachineName}-{i}";
            tasks.Add(Task.Run(async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await using var c = log.Subscribe(settings.Kafka.GroupId, [Topics.PlatformEvents], name);
                    try { await consumer.RunAsync(c, clock, stoppingToken); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "consumer {Name} failed; restarting", name);
                        await Task.Delay(TimeSpan.FromSeconds(2), clock, stoppingToken).ConfigureAwait(false);
                    }
                }
            }, stoppingToken));
        }
        tasks.Add(Every(settings.RelayInterval, async ct =>
        {
            var report = await relay.RunOnceAsync(ct);
            state.LastRelay = report;
            state.LastRelayAt = clock.GetUtcNow();
            state.Published += report.Published;
        }, stoppingToken));
        tasks.Add(Every(settings.ReconcileInterval, async ct =>
        {
            state.LastReconcile = await reconciler.RunOnceAsync(ct);
            state.LastReconcileAt = clock.GetUtcNow();
        }, stoppingToken, initialDelay: settings.ReconcileInterval));
        return Task.WhenAll(tasks);
    }

    private Task Every(TimeSpan interval, Func<CancellationToken, Task> work, CancellationToken ct, TimeSpan? initialDelay = null) => Task.Run(async () =>
    {
        if (initialDelay is { } d) await Delay(d, ct);
        while (!ct.IsCancellationRequested)
        {
            try { await work(ct); }
            catch (OperationCanceledException) { return; }
            catch (TransientException ex) { logger.LogWarning("throttled or unavailable: {Message}", ex.Message); }
            catch (Exception ex) { logger.LogError(ex, "background work failed"); }
            await Delay(interval, ct);
        }
    }, ct);

    private async Task Delay(TimeSpan d, CancellationToken ct)
    {
        try { await Task.Delay(d, clock, ct); } catch (OperationCanceledException) { }
    }
}
