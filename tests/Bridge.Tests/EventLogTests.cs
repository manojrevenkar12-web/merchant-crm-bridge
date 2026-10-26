using Bridge.Core.Messaging;

namespace Bridge.Tests;

public class EventLogTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task One_key_stays_on_one_partition_in_order()
    {
        var log = new InMemoryEventLog(partitions: 4);
        for (var i = 0; i < 5; i++) await log.ProduceAsync("t", "M-000001", $"m{i}", null, _ct);
        await using var consumer = log.Subscribe("g", ["t"], "c1");
        var got = new List<string>();
        while (await consumer.ConsumeAsync(TimeSpan.FromMilliseconds(50), _ct) is { } m)
        {
            got.Add(m.Value);
            consumer.Commit(m);
        }
        Assert.Equal(["m0", "m1", "m2", "m3", "m4"], got);
    }

    [Fact]
    public async Task Uncommitted_messages_are_delivered_again_after_a_crash()
    {
        var log = new InMemoryEventLog(partitions: 1);
        await log.ProduceAsync("t", "k", "a", null, _ct);
        await log.ProduceAsync("t", "k", "b", null, _ct);
        var first = log.Subscribe("g", ["t"], "c1");
        var a = await first.ConsumeAsync(TimeSpan.FromMilliseconds(50), _ct);
        first.Commit(a!);
        var b = await first.ConsumeAsync(TimeSpan.FromMilliseconds(50), _ct);
        Assert.Equal("b", b!.Value);
        await first.DisposeAsync(); // crashed before committing b

        await using var second = log.Subscribe("g", ["t"], "c2");
        var again = await second.ConsumeAsync(TimeSpan.FromMilliseconds(50), _ct);
        Assert.Equal("b", again!.Value);
        Assert.Equal(2, again.DeliveryCount);
    }

    [Fact]
    public async Task Rewind_redelivers_the_same_message_and_counts_deliveries()
    {
        var log = new InMemoryEventLog(partitions: 1);
        await log.ProduceAsync("t", "k", "a", null, _ct);
        await using var c = log.Subscribe("g", ["t"], "c1");
        var m = await c.ConsumeAsync(TimeSpan.FromMilliseconds(50), _ct);
        c.Rewind(m!);
        var m2 = await c.ConsumeAsync(TimeSpan.FromMilliseconds(50), _ct);
        Assert.Equal(m!.Offset, m2!.Offset);
        Assert.Equal(2, m2.DeliveryCount);
    }

    [Fact]
    public async Task A_second_consumer_takes_over_partitions_and_lag_is_reported()
    {
        var log = new InMemoryEventLog(partitions: 2);
        for (var i = 0; i < 20; i++) await log.ProduceAsync("t", $"M-{i:000000}", "x", null, _ct);
        await using var c1 = log.Subscribe("g", ["t"], "c1");
        await using var c2 = log.Subscribe("g", ["t"], "c2");
        var seen = new HashSet<(int, long)>();
        foreach (var c in new[] { c1, c2 })
            while (await c.ConsumeAsync(TimeSpan.FromMilliseconds(20), _ct) is { } m) { seen.Add((m.Partition, m.Offset)); c.Commit(m); }
        Assert.Equal(20, seen.Count);
        Assert.All((await log.GetLagAsync("g", "t", _ct)).Values, lag => Assert.Equal(0, lag));
        Assert.Equal(20, (await log.GetLagAsync("other", "t", _ct)).Values.Sum());
    }
}
