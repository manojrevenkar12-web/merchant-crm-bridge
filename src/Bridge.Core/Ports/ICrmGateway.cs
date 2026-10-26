using Bridge.Core.Domain;

namespace Bridge.Core.Ports;

/// <summary>What the bridge needs from Dynamics 365. Implemented over the Dataverse Web API.</summary>
public interface ICrmGateway
{
    Task<CrmMerchant?> GetMerchantAsync(string merchantId, CancellationToken ct);

    Task<bool> InboundEventSeenAsync(string eventId, CancellationToken ct);

    /// <summary>Writes the effect and the inbound-event ledger row in one transaction ($batch changeset).
    /// Throws <see cref="ConcurrencyException"/> if the merchant row changed since it was read, and
    /// <see cref="DuplicateEventException"/> if the event was already recorded.</summary>
    Task CommitAsync(CrmMerchant read, Effect effect, InboundRecord ledger, CancellationToken ct);

    Task<IReadOnlyList<OutboxMessage>> GetPendingOutboxAsync(int max, CancellationToken ct);

    Task MarkOutboxSentAsync(OutboxMessage message, DateTimeOffset at, CancellationToken ct);

    Task MarkOutboxFailedAsync(OutboxMessage message, string error, CancellationToken ct);

    /// <summary>Merchants the platform knows (past prospect), in pages, for reconciliation.</summary>
    Task<CrmPage<CrmMerchant>> ListSyncedMerchantsAsync(string? continuation, int pageSize, CancellationToken ct);

    Task WriteDriftAsync(DriftFinding finding, CancellationToken ct);

    /// <summary>Takes or renews a named lease (optimistic concurrency on the lease row).</summary>
    Task<bool> TryAcquireLeaseAsync(string name, string holder, TimeSpan duration, CancellationToken ct);
}

public sealed record CrmPage<T>(IReadOnlyList<T> Items, string? Continuation);

public sealed record InboundRecord(string EventId, string EventType, string MerchantId, long Sequence, Outcome Outcome, string Detail, DateTimeOffset ProcessedOn);

public sealed record OutboxMessage(Guid RowId, string ETag, string EventId, string EventType, string MerchantId, long Sequence, int SchemaVersion, string PayloadJson, DateTimeOffset CreatedOn, int Attempts);

public sealed record DriftFinding(string MerchantId, string Field, string? CrmValue, string? PlatformValue, bool Healed, DateTimeOffset DetectedOn);

/// <summary>The row changed between read and write (HTTP 412 on If-Match). Re-read and decide again.</summary>
public sealed class ConcurrencyException : Exception
{
    public ConcurrencyException(string message) : base(message) { }
    public ConcurrencyException() { }
    public ConcurrencyException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The inbound event is already in the ledger: another delivery of it was applied.</summary>
public sealed class DuplicateEventException : Exception
{
    public DuplicateEventException(string message) : base(message) { }
    public DuplicateEventException() { }
    public DuplicateEventException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Worth retrying later: throttling (429 with Retry-After), 5xx, timeouts.</summary>
public sealed class TransientException : Exception
{
    public TransientException(string message, TimeSpan? retryAfter = null, Exception? inner = null) : base(message, inner)
    {
        RetryAfter = retryAfter;
    }

    public TransientException() { }
    public TransientException(string message) : base(message) { }
    public TransientException(string message, Exception inner) : base(message, inner) { }

    public TimeSpan? RetryAfter { get; }
}

/// <summary>The merchant is not in the CRM. Usually permanent (the platform knows only merchants the
/// CRM sent), but retried a few times in case the CRM write is still replicating.</summary>
public sealed class UnknownMerchantException : Exception
{
    public UnknownMerchantException(string message) : base(message) { }
    public UnknownMerchantException() { }
    public UnknownMerchantException(string message, Exception inner) : base(message, inner) { }
}
