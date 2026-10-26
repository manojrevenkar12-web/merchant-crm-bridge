using Bridge.Core.Ports;

namespace Bridge.Core.Messaging;

/// <summary>
/// A Kafka-like log in memory with the delivery rules the bridge must survive: partitions by key,
/// order within a partition only, consumer groups with committed offsets, at-least-once delivery
/// (anything not committed is delivered again after a rebalance or a crash), and rebalancing when
/// consumers join or leave a group. Used by unit tests and the drills; the Confluent client is used
/// against real Kafka in the integration tests.
/// </summary>
public sealed class InMemoryEventLog(int partitions = 3) : IEventLog
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<StoredMessage>[]> _topics = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Group, string Topic, int Partition), long> _committed = [];
    private readonly Dictionary<(string Group, string Topic, int Partition, long Offset), int> _deliveries = [];
    private readonly Dictionary<string, List<Consumer>> _groups = new(StringComparer.Ordinal);
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Partitions => partitions;

    private sealed record StoredMessage(string Key, string Value, IReadOnlyDictionary<string, string> Headers);

    public static int PartitionFor(string key, int count)
    {
        // FNV-1a: stable across processes, unlike string.GetHashCode.
        unchecked
        {
            var hash = 2166136261u;
            foreach (var b in System.Text.Encoding.UTF8.GetBytes(key)) hash = (hash ^ b) * 16777619u;
            return (int)(hash % (uint)count);
        }
    }

    private List<StoredMessage>[] Topic(string name)
    {
        if (!_topics.TryGetValue(name, out var parts))
        {
            parts = Enumerable.Range(0, partitions).Select(_ => new List<StoredMessage>()).ToArray();
            _topics[name] = parts;
        }
        return parts;
    }

    public Task<LogPosition> ProduceAsync(string topic, string key, string value, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        LogPosition position;
        TaskCompletionSource signal;
        lock (_gate)
        {
            var p = PartitionFor(key, partitions);
            var list = Topic(topic)[p];
            list.Add(new StoredMessage(key, value, headers is null ? new Dictionary<string, string>() : new Dictionary<string, string>(headers)));
            position = new LogPosition(topic, p, list.Count - 1);
            signal = _signal;
            _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        signal.TrySetResult();
        return Task.FromResult(position);
    }

    public IEventConsumer Subscribe(string groupId, IReadOnlyCollection<string> topics, string consumerName)
    {
        lock (_gate)
        {
            var consumer = new Consumer(this, groupId, topics.ToArray(), consumerName);
            if (!_groups.TryGetValue(groupId, out var members)) _groups[groupId] = members = [];
            members.Add(consumer);
            Rebalance(groupId);
            return consumer;
        }
    }

    public Task<IReadOnlyList<LogMessage>> ReadTopicAsync(string topic, int max, CancellationToken ct)
    {
        lock (_gate)
        {
            var result = new List<LogMessage>();
            var parts = Topic(topic);
            for (var p = 0; p < parts.Length; p++)
                for (var o = 0; o < parts[p].Count && result.Count < max; o++)
                    result.Add(new LogMessage(topic, p, o, parts[p][o].Key, parts[p][o].Value, parts[p][o].Headers, 1));
            return Task.FromResult<IReadOnlyList<LogMessage>>(result);
        }
    }

    public Task<IReadOnlyDictionary<int, long>> GetLagAsync(string groupId, string topic, CancellationToken ct)
    {
        lock (_gate)
        {
            var parts = Topic(topic);
            var lag = new Dictionary<int, long>();
            for (var p = 0; p < parts.Length; p++)
                lag[p] = parts[p].Count - _committed.GetValueOrDefault((groupId, topic, p));
            return Task.FromResult<IReadOnlyDictionary<int, long>>(lag);
        }
    }

    public long Count(string topic)
    {
        lock (_gate) return Topic(topic).Sum(p => (long)p.Count);
    }

    // Assigns (topic, partition) pairs round-robin over the group's members; a member's position on a
    // partition it newly owns starts at the group's committed offset, so uncommitted work is redone.
    private void Rebalance(string groupId)
    {
        var members = _groups[groupId];
        foreach (var m in members) m.Assigned.Clear();
        if (members.Count == 0) return;
        var pairs = members.SelectMany(m => m.Topics).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal)
            .SelectMany(t => Enumerable.Range(0, partitions).Select(p => (t, p))).ToList();
        for (var i = 0; i < pairs.Count; i++)
        {
            var owner = members[i % members.Count];
            owner.Assigned[pairs[i]] = _committed.GetValueOrDefault((groupId, pairs[i].t, pairs[i].p));
        }
    }

    private sealed class Consumer(InMemoryEventLog log, string group, string[] topics, string name) : IEventConsumer
    {
        public string Name { get; } = name;
        public string[] Topics { get; } = topics;
        public Dictionary<(string t, int p), long> Assigned { get; } = [];
        private int _cursor;
        private bool _disposed;

        public async Task<LogMessage?> ConsumeAsync(TimeSpan wait, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + wait;
            while (true)
            {
                Task signal;
                lock (log._gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    var keys = Assigned.Keys.OrderBy(k => k.t, StringComparer.Ordinal).ThenBy(k => k.p).ToList();
                    for (var i = 0; i < keys.Count; i++)
                    {
                        var key = keys[(_cursor + i) % keys.Count];
                        var position = Assigned[key];
                        var list = log.Topic(key.t)[key.p];
                        if (position < list.Count)
                        {
                            _cursor = (_cursor + i + 1) % Math.Max(1, keys.Count);
                            Assigned[key] = position + 1;
                            var dk = (group, key.t, key.p, position);
                            var count = log._deliveries.GetValueOrDefault(dk) + 1;
                            log._deliveries[dk] = count;
                            var m = list[(int)position];
                            return new LogMessage(key.t, key.p, position, m.Key, m.Value, m.Headers, count);
                        }
                    }
                    signal = log._signal.Task;
                }
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) return null;
                await Task.WhenAny(signal, Task.Delay(remaining, ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
        }

        public void Commit(LogMessage message)
        {
            lock (log._gate)
            {
                var k = (group, message.Topic, message.Partition);
                if (message.Offset + 1 > log._committed.GetValueOrDefault(k)) log._committed[k] = message.Offset + 1;
            }
        }

        public void Rewind(LogMessage message)
        {
            lock (log._gate)
            {
                if (Assigned.ContainsKey((message.Topic, message.Partition))) Assigned[(message.Topic, message.Partition)] = message.Offset;
            }
        }

        /// <summary>Leaving the group without committing: like a crash, the next owner redelivers.</summary>
        public ValueTask DisposeAsync()
        {
            lock (log._gate)
            {
                if (_disposed) return ValueTask.CompletedTask;
                _disposed = true;
                log._groups[group].Remove(this);
                log.Rebalance(group);
            }
            return ValueTask.CompletedTask;
        }
    }
}
