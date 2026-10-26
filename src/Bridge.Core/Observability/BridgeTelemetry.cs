using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Bridge.Core.Observability;

/// <summary>Metrics and traces in the .NET built-in APIs, so any exporter (Application Insights,
/// OpenTelemetry) picks them up. Kafka messages carry W3C traceparent headers, so one trace follows a
/// change from the CRM to the platform and back.</summary>
public sealed class BridgeTelemetry : IDisposable
{
    public const string Name = "MerchantCrmBridge";

    public static readonly ActivitySource Activities = new(Name);

    private readonly Meter _meter;

    public BridgeTelemetry(IMeterFactory? factory = null)
    {
        _meter = factory?.Create(Name) ?? new Meter(Name);
        Consumed = _meter.CreateCounter<long>("bridge.platform_events", description: "Platform events handled, by type and outcome");
        DeadLettered = _meter.CreateCounter<long>("bridge.dead_lettered", description: "Messages sent to the dead-letter topic, by reason");
        Published = _meter.CreateCounter<long>("bridge.outbox_published", description: "CRM changes published to Kafka");
        PublishFailures = _meter.CreateCounter<long>("bridge.outbox_failures");
        ConcurrencyRetries = _meter.CreateCounter<long>("bridge.concurrency_retries", description: "Re-reads after the merchant row changed underneath");
        Throttled = _meter.CreateCounter<long>("bridge.throttled", description: "Waits after Dataverse service protection (429)");
        Drift = _meter.CreateCounter<long>("bridge.drift", description: "Differences found by the reconciler, by resolution");
        ApplyDuration = _meter.CreateHistogram<double>("bridge.apply_duration", unit: "ms");
    }

    public Counter<long> Consumed { get; }
    public Counter<long> DeadLettered { get; }
    public Counter<long> Published { get; }
    public Counter<long> PublishFailures { get; }
    public Counter<long> ConcurrencyRetries { get; }
    public Counter<long> Throttled { get; }
    public Counter<long> Drift { get; }
    public Histogram<double> ApplyDuration { get; }

    public void Dispose() => _meter.Dispose();
}

public static class TraceContext
{
    public const string Header = "traceparent";

    public static Dictionary<string, string> Inject(Activity? activity)
    {
        var headers = new Dictionary<string, string>();
        if (activity is not null) headers[Header] = activity.Id!;
        return headers;
    }

    public static ActivityContext Extract(IReadOnlyDictionary<string, string> headers) =>
        headers.TryGetValue(Header, out var tp) && ActivityContext.TryParse(tp, null, out var ctx) ? ctx : default;
}
