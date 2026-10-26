namespace Bridge.Core.Ports;

/// <summary>A partitioned, ordered log with consumer groups: Apache Kafka in production, an in-memory
/// log with the same delivery rules in tests and drills.</summary>
public interface IEventLog
{
    /// <summary>Returns when the broker has acknowledged the write (acks=all, idempotent producer).</summary>
    Task<LogPosition> ProduceAsync(string topic, string key, string value, IReadOnlyDictionary<string, string>? headers, CancellationToken ct);

    IEventConsumer Subscribe(string groupId, IReadOnlyCollection<string> topics, string consumerName);

    /// <summary>Reads a topic from the beginning without committing anything (operations: the dead-letter topic).</summary>
    Task<IReadOnlyList<LogMessage>> ReadTopicAsync(string topic, int max, CancellationToken ct);

    /// <summary>Messages not yet committed by the group, per partition.</summary>
    Task<IReadOnlyDictionary<int, long>> GetLagAsync(string groupId, string topic, CancellationToken ct);
}

public sealed record LogPosition(string Topic, int Partition, long Offset);

public sealed record LogMessage(string Topic, int Partition, long Offset, string Key, string Value, IReadOnlyDictionary<string, string> Headers, int DeliveryCount);

/// <summary>At-least-once delivery: a message is delivered again until its offset is committed.</summary>
public interface IEventConsumer : IAsyncDisposable
{
    Task<LogMessage?> ConsumeAsync(TimeSpan wait, CancellationToken ct);

    /// <summary>Commits the message's offset + 1 for its partition.</summary>
    void Commit(LogMessage message);

    /// <summary>Delivers this message again (and everything after it on its partition) on the next consume.</summary>
    void Rewind(LogMessage message);
}
