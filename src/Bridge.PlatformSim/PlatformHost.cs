using Bridge.Core.Contracts;
using Bridge.Core.Ports;

namespace Bridge.PlatformSim;

public static class PlatformHost
{
    /// <summary>The simulator as a web app: GraphQL at /graphql (bearer token required), the CRM event
    /// pump and the ticking world in the background.</summary>
    public static WebApplication Build(string[] args, IEventLog log, Simulator simulator, TimeSpan? tick = null, string expectedToken = "emu.app.merchant-crm-bridge")
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(simulator);
        builder.Services.AddGraphQLServer().AddQueryType<Query>();
        var app = builder.Build();
        app.Use(async (http, next) =>
        {
            if (http.Request.Path.StartsWithSegments("/graphql", StringComparison.Ordinal) && http.Request.Headers.Authorization != "Bearer " + expectedToken)
            {
                http.Response.StatusCode = 401;
                return;
            }
            await next(http).ConfigureAwait(false);
        });
        app.MapGraphQL();
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/_sim/stats", () => Results.Json(new
        {
            merchants = simulator.Merchants.Count,
            simulator.Published,
            simulator.Duplicated,
            simulator.Reordered,
            dropped = simulator.DroppedIds.Count,
            poison = simulator.PoisonIds.Count,
            simulator.Ticks,
        }));

        if (tick is { } interval)
        {
            var pump = new CrmEventPump(log, simulator);
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                var stop = app.Lifetime.ApplicationStopping;
                _ = Task.Run(() => pump.RunAsync(stop), stop);
                _ = Task.Run(async () =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        try
                        {
                            await simulator.TickAsync(stop).ConfigureAwait(false);
                            await Task.Delay(interval, stop).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { return; }
                    }
                }, stop);
            });
        }
        return app;
    }
}
