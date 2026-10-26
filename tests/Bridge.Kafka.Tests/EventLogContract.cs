using Bridge.Core.Messaging;
using Bridge.Core.Ports;
using Bridge.Kafka;

namespace Bridge.Kafka.Tests;

/// <summary>The delivery rules every <see cref="IEventLog"/> must keep; the consumer, relay and drills rely on them.</summary>
public abstract class EventLogContract
{
    protected CancellationToken Ct { get; } = TestContext.Current.CancellationToken;
    protected static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    /// <summary>A log with fresh topics; <paramref name="partitions"/> per topic.</summary>
    protected abstract Task<(IEventLog Log, string Topic)> CreateAsync(int partitions);

    protected static async Task<LogMessage?> NextAsync(IEventConsumer c, CancellationToken ct, int attempts = 15)
    {
        for (var i = 0; i < attempts; i++)
            if (await c.ConsumeAsync(Wait, ct) is { } m) return m;
        return null;
    }

    [Fact]
    public async Task One_key_keeps_its_order_and_headers_survive()
    {
        var (log, topic) = await CreateAsync(3);
        var headers = new Dictionary<string, string> { ["traceparent"] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01", ["schemaversion"] = "2" };
        var positions = new List<LogPosition>();
        for (var i = 0; i < 6; i++) positions.Add(await log.ProduceAsync(topic, "M-000042", $"m{i}", headers, Ct));
        Assert.Single(positions.Select(p => p.Partition).Distinct());

        await using var c = log.Subscribe("g-" + Guid.NewGuid().ToString("N"), [topic], "c1");
        var got = new List<LogMessage>();
        while (got.Count < 6 && await NextAsync(c, Ct) is { } m) { got.Add(m); c.Commit(m); }
        Assert.Equal(["m0", "m1", "m2", "m3", "m4", "m5"], got.Select(m => m.Value));
        Assert.All(got, m => Assert.Equal(headers, m.Headers));
        Assert.All(got, m => Assert.Equal(1, m.DeliveryCount));
    }

    [Fact]
    public async Task Rewind_delivers_the_same_message_again_before_anything_after_it()
    {
        var (log, topic) = await CreateAsync(1);
        await log.ProduceAsync(topic, "k", "a", null, Ct);
        await log.ProduceAsync(topic, "k", "b", null, Ct);
        await using var c = log.Subscribe("g-" + Guid.NewGuid().ToString("N"), [topic], "c1");
        var first = await NextAsync(c, Ct);
        Assert.Equal("a", first!.Value);
        c.Rewind(first);
        var again = await NextAsync(c, Ct);
        Assert.Equal(("a", first.Offset, 2), (again!.Value, again.Offset, again.DeliveryCount));
        c.Commit(again);
        Assert.Equal("b", (await NextAsync(c, Ct))!.Value);
    }

    [Fact]
    public async Task What_was_not_committed_is_delivered_to_the_next_consumer_of_the_group()
    {
        var (log, topic) = await CreateAsync(1);
        var group = "g-" + Guid.NewGuid().ToString("N");
        await log.ProduceAsync(topic, "k", "a", null, Ct);
        await log.ProduceAsync(topic, "k", "b", null, Ct);
        var first = log.Subscribe(group, [topic], "c1");
        var a = await NextAsync(first, Ct);
        first.Commit(a!);
        Assert.Equal("b", (await NextAsync(first, Ct))!.Value);
        await first.DisposeAsync(); // gone before committing b

        await using var second = log.Subscribe(group, [topic], "c2");
        Assert.Equal("b", (await NextAsync(second, Ct))!.Value);
    }

    [Fact]
    public async Task Lag_counts_what_the_group_has_not_committed()
    {
        var (log, topic) = await CreateAsync(2);
        var group = "g-" + Guid.NewGuid().ToString("N");
        for (var i = 0; i < 10; i++) await log.ProduceAsync(topic, $"M-{i:000000}", "x", null, Ct);
        await using (var c = log.Subscribe(group, [topic], "c1"))
        {
            for (var i = 0; i < 4; i++) c.Commit((await NextAsync(c, Ct))!);
        }
        Assert.Equal(6, (await log.GetLagAsync(group, topic, Ct)).Values.Sum());
        Assert.Equal(10, (await log.ReadTopicAsync(topic, 100, Ct)).Count); // reading for operations commits nothing
        Assert.Equal(6, (await log.GetLagAsync(group, topic, Ct)).Values.Sum());
    }
}

public sealed class InMemoryLogContract : EventLogContract
{
    protected override Task<(IEventLog Log, string Topic)> CreateAsync(int partitions) =>
        Task.FromResult<(IEventLog, string)>((new InMemoryEventLog(partitions), "t"));
}

public sealed class KafkaLogContract : EventLogContract, IDisposable
{
    private readonly List<KafkaEventLog> _logs = [];

    protected override async Task<(IEventLog Log, string Topic)> CreateAsync(int partitions)
    {
        var bootstrap = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP");
        if (string.IsNullOrWhiteSpace(bootstrap)) Assert.Skip("KAFKA_BOOTSTRAP is not set (CI runs these against a Kafka broker)");
        var log = new KafkaEventLog(new KafkaOptions { BootstrapServers = bootstrap, Partitions = partitions });
        _logs.Add(log);
        var topic = "contract-" + Guid.NewGuid().ToString("N")[..12];
        await log.EnsureTopicsAsync([topic], Ct);
        return (log, topic);
    }

    public void Dispose()
    {
        foreach (var log in _logs) log.Dispose();
    }
}
