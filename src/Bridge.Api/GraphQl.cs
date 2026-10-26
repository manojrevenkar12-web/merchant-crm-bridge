namespace Bridge.Api;

/// <summary>Read-only GraphQL for CRM extensions and dashboards that need a merchant's whole picture in one
/// request (CRM, platform and sync state). Commands stay on REST, where each has its own permission.</summary>
public sealed class BridgeQuery
{
    public Task<MerchantDetail?> Merchant(string id, [Service] OpsQueries q, CancellationToken ct) =>
        Endpoints.MerchantId().IsMatch(id) ? q.MerchantAsync(id, ct)
            : throw new GraphQLException(ErrorBuilder.New().SetMessage("Merchant ids look like M-000123.").SetCode("INVALID_MERCHANT_ID").Build());

    public Task<IReadOnlyList<MerchantSummary>> Merchants(string? search, string? stage, int? first, [Service] OpsQueries q, CancellationToken ct) =>
        q.MerchantsAsync(search, stage, first ?? 50, ct);

    public Task<Overview> Overview([Service] OpsQueries q, CancellationToken ct) => q.OverviewAsync(ct);

    public Task<IReadOnlyList<DeadLetter>> DeadLetters([Service] OpsQueries q, CancellationToken ct) => q.DeadLettersAsync(ct);

    public Task<IReadOnlyList<DriftItem>> Drift([Service] OpsQueries q, CancellationToken ct) => q.DriftAsync(ct);
}
