using Bridge.Core.Contracts;

namespace Bridge.Core.Ports;

/// <summary>The payment platform's merchant API (GraphQL), used to reconcile.</summary>
public interface IPlatformApi
{
    Task<MerchantSnapshot?> GetSnapshotAsync(string merchantId, CancellationToken ct);

    Task<IReadOnlyDictionary<string, MerchantSnapshot>> GetSnapshotsAsync(IReadOnlyCollection<string> merchantIds, CancellationToken ct);
}
