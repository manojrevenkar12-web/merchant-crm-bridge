using System.Collections.Concurrent;
using System.Text;
using Bridge.Core.Ports;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using LogMessage = Bridge.Core.Ports.LogMessage;

namespace Bridge.Kafka;

/// <summary>
/// <see cref="IEventLog"/> on Apache Kafka. The producer is idempotent with acks=all, so a retried send
/// does not create a duplicate on the broker and an acknowledged send survives a broker failure. Consumers
/// commit manually, after the CRM write: at-least-once delivery, which the applier turns into exactly-once
/// effects with its ledger.
/// </summary>
public sealed class KafkaEventLog : IEventLog, IDisposable
{
    private readonly KafkaOptions _options;
    private readonly IProducer<string, string> _producer;

    public KafkaEventLog(KafkaOptions options)
    {
        _options = options;
        var config = new ProducerConfig
        {
            EnableIdempotence = true,
            Acks = Acks.All,
            LingerMs = 5,
            CompressionType = CompressionType.Zstd,
            MessageTimeoutMs = 30_000,
        };
        options.Apply(config);
        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    public async Task EnsureTopicsAsync(IEnumerable<string> topics, CancellationToken ct)
    {
        var config = new AdminClientConfig();
        _options.Apply(config);
        using var admin = new AdminClientBuilder(config).Build();
        var existing = admin.GetMetadata(TimeSpan.FromSeconds(10)).Topics.Select(t => t.Topic).ToHashSet(StringComparer.Ordinal);
        var missing = topics.Where(t => !existing.Contains(t)).Select(t => new TopicSpecification
        {
            Name = t,
            NumPartitions = _options.Partitions,
            ReplicationFactor = _options.ReplicationFactor,
        }).ToList();
        if (missing.Count == 0) return;
        try
        {
            await admin.CreateTopicsAsync(missing).ConfigureAwait(false);
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code is ErrorCode.TopicAlreadyExists or ErrorCode.NoError))
        {
            // created concurrently
        }
    }

    public async Task<LogPosition> ProduceAsync(string topic, string key, string value, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        var message = new Message<string, string> { Key = key, Value = value, Headers = [] };
        if (headers is not null) foreach (var (k, v) in headers) message.Headers.Add(k, Encoding.UTF8.GetBytes(v));
        var result = await _producer.ProduceAsync(topic, message, ct).ConfigureAwait(false);
        return new LogPosition(result.Topic, result.Partition.Value, result.Offset.Value);
    }

    public IEventConsumer Subscribe(string groupId, IReadOnlyCollection<string> topics, string consumerName)
    {
        var config = new ConsumerConfig
        {
            GroupId = groupId,
            ClientId = consumerName,
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky,
            IsolationLevel = IsolationLevel.ReadCommitted,
            MaxPollIntervalMs = 300_000,
        };
        _options.Apply(config);
        return new Consumer(config, topics);
    }

    public Task<IReadOnlyList<LogMessage>> ReadTopicAsync(string topic, int max, CancellationToken ct) => Task.Run<IReadOnlyList<LogMessage>>(() =>
    {
        var config = new ConsumerConfig { GroupId = "inspect-" + Guid.NewGuid().ToString("N"), EnableAutoCommit = false, AutoOffsetReset = AutoOffsetReset.Earliest };
        _options.Apply(config);
        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        var partitions = Partitions(topic);
        var high = partitions.ToDictionary(p => p, p => consumer.QueryWatermarkOffsets(new TopicPartition(topic, p), TimeSpan.FromSeconds(5)).High.Value);
        var assigned = partitions.Where(p => high[p] > 0).Select(p => new TopicPartitionOffset(topic, p, Offset.Beginning)).ToList();
        var result = new List<LogMessage>();
        if (assigned.Count == 0) return result;
        consumer.Assign(assigned);
        var done = new HashSet<int>();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (result.Count < max && done.Count < assigned.Count && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var r = consumer.Consume(TimeSpan.FromMilliseconds(500));
            if (r is null) continue;
            result.Add(ToMessage(r, 1));
            if (r.Offset.Value + 1 >= high[r.Partition.Value]) done.Add(r.Partition.Value);
        }
        consumer.Close();
        return result;
    }, ct);

    public Task<IReadOnlyDictionary<int, long>> GetLagAsync(string groupId, string topic, CancellationToken ct) => Task.Run<IReadOnlyDictionary<int, long>>(() =>
    {
        var config = new ConsumerConfig { GroupId = groupId, EnableAutoCommit = false };
        _options.Apply(config);
        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        var tps = Partitions(topic).Select(p => new TopicPartition(topic, p)).ToList();
        var committed = consumer.Committed(tps, TimeSpan.FromSeconds(10)).ToDictionary(c => c.Partition.Value, c => c.Offset.Value < 0 ? 0 : c.Offset.Value);
        return tps.ToDictionary(tp => tp.Partition.Value, tp => consumer.QueryWatermarkOffsets(tp, TimeSpan.FromSeconds(5)).High.Value - committed.GetValueOrDefault(tp.Partition.Value));
    }, ct);

    private List<int> Partitions(string topic)
    {
        var config = new AdminClientConfig();
        _options.Apply(config);
        using var admin = new AdminClientBuilder(config).Build();
        var meta = admin.GetMetadata(topic, TimeSpan.FromSeconds(10)).Topics.Single();
        return meta.Error.IsError ? [] : meta.Partitions.Select(p => p.PartitionId).ToList();
    }

    private static LogMessage ToMessage(ConsumeResult<string, string> r, int deliveries) => new(
        r.Topic, r.Partition.Value, r.Offset.Value, r.Message.Key, r.Message.Value,
        r.Message.Headers?.ToDictionary(h => h.Key, h => Encoding.UTF8.GetString(h.GetValueBytes()), StringComparer.Ordinal) ?? new Dictionary<string, string>(),
        deliveries);

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(10));
        _producer.Dispose();
    }

    private sealed class Consumer : IEventConsumer
    {
        private readonly IConsumer<string, string> _consumer;
        private readonly ConcurrentDictionary<(string, int, long), int> _deliveries = new();

        public Consumer(ConsumerConfig config, IReadOnlyCollection<string> topics)
        {
            _consumer = new ConsumerBuilder<string, string>(config)
                .SetPartitionsRevokedHandler((consumer, revoked) =>
                {
                    foreach (var key in _deliveries.Keys.Where(k => revoked.Any(r => r.Topic == k.Item1 && r.Partition.Value == k.Item2))) _deliveries.TryRemove(key, out _);
                })
                .Build();
            _consumer.Subscribe(topics);
        }

        public Task<LogMessage?> ConsumeAsync(TimeSpan wait, CancellationToken ct) => Task.Run(() =>
        {
            ConsumeResult<string, string>? r;
            try
            {
                r = _consumer.Consume(wait);
            }
            catch (ConsumeException ex) when (!ex.Error.IsFatal)
            {
                return null;
            }
            if (r is null || r.IsPartitionEOF) return null;
            var count = _deliveries.AddOrUpdate((r.Topic, r.Partition.Value, r.Offset.Value), 1, (_, c) => c + 1);
            return (LogMessage?)ToMessage(r, count);
        }, ct);

        public void Commit(LogMessage message)
        {
            _consumer.Commit([new TopicPartitionOffset(message.Topic, message.Partition, message.Offset + 1)]);
            _deliveries.TryRemove((message.Topic, message.Partition, message.Offset), out _);
        }

        public void Rewind(LogMessage message) =>
            _consumer.Seek(new TopicPartitionOffset(message.Topic, message.Partition, message.Offset));

        public ValueTask DisposeAsync()
        {
            try { _consumer.Close(); } catch (KafkaException) { /* leaving the group anyway */ }
            _consumer.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
