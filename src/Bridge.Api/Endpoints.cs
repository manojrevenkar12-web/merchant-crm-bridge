using System.Security.Claims;
using System.Text.RegularExpressions;
using Bridge.Core.Services;

namespace Bridge.Api;

/// <summary>The REST API (contracts/openapi.json, served at /openapi.json). Reads need Sync.Read, commands Sync.Operate; errors are
/// RFC 9457 problem details.</summary>
public static partial class Endpoints
{
    [GeneratedRegex("^M-[0-9]{6}$")]
    internal static partial Regex MerchantId();

    public static void MapBridgeApi(this WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
        app.MapGet("/openapi.json", () => Results.File(System.IO.Path.Combine(AppContext.BaseDirectory, "openapi.json"), "application/json")).AllowAnonymous().ExcludeFromDescription();

        var v1 = app.MapGroup("/v1").RequireAuthorization(Auth.Read);

        v1.MapGet("/overview", (OpsQueries q, CancellationToken ct) => q.OverviewAsync(ct));

        v1.MapGet("/merchants", async (string? search, string? stage, int? top, OpsQueries q, CancellationToken ct) =>
        {
            try { return Results.Ok(await q.MerchantsAsync(search, stage, top ?? 100, ct)); }
            catch (ArgumentException ex) { return Problem(400, "Invalid filter", ex.Message); }
        });

        v1.MapGet("/merchants/{merchantId}", async (string merchantId, OpsQueries q, CancellationToken ct) =>
        {
            if (!MerchantId().IsMatch(merchantId)) return Problem(400, "Invalid merchant id", "Merchant ids look like M-000123.");
            return await q.MerchantAsync(merchantId, ct) is { } m ? Results.Ok(m) : Problem(404, "Merchant not found", $"No merchant {merchantId} in the CRM.");
        });

        v1.MapGet("/dead-letters", (OpsQueries q, CancellationToken ct) => q.DeadLettersAsync(ct));

        v1.MapPost("/dead-letters/{partition:int}/{offset:long}/replay", async (int partition, long offset, ClaimsPrincipal user, OpsQueries q, CancellationToken ct) =>
        {
            var name = user.FindFirst("name")?.Value ?? user.FindFirst("oid")?.Value ?? "unknown";
            return await q.ReplayAsync(partition, offset, name, ct) switch
            {
                null => Problem(404, "Dead letter not found", $"Nothing at partition {partition}, offset {offset}."),
                false => Problem(409, "Already replayed", "This dead letter was replayed before; replaying it again would only duplicate it."),
                true => Results.Accepted(),
            };
        }).RequireAuthorization(Auth.Operate);

        v1.MapGet("/drift", (OpsQueries q, CancellationToken ct) => q.DriftAsync(ct));

        v1.MapPost("/reconcile", async (Reconciler reconciler, WorkerState state, TimeProvider clock, CancellationToken ct) =>
        {
            var report = await reconciler.RunOnceAsync(ct);
            if (!report.LeaseHeld) return Problem(409, "Reconciliation is running", "Another reconciler holds the lease; try again in a few minutes.");
            state.LastReconcile = report;
            state.LastReconcileAt = clock.GetUtcNow();
            return Results.Ok(report);
        }).RequireAuthorization(Auth.Operate);
    }

    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);
}
