using System.Text.Json.Nodes;

namespace Bridge.Emulator;

/// <summary>Builds the emulator as a web app, standalone (docker compose) or inside a test or drill on
/// a free port.</summary>
public static class EmulatorHost
{
    public static WebApplication Build(string[] args, EmulatorOptions options, TimeProvider? clock = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var time = clock ?? TimeProvider.System;
        var store = new Store(Metadata.Load());
        var engine = new Engine(store, options, time);
        var api = new WebApi(engine, time);
        builder.Services.AddSingleton(options).AddSingleton(engine).AddSingleton(api);
        var app = builder.Build();

        app.MapPost("/{tenant}/oauth2/v2.0/token", (HttpContext http) => WebApi.TokenEndpoint(http, options));
        app.Map(WebApi.Root + "{**path}", api.HandleAsync);

        // Operations for tests and drills: reset, fault injection, counters.
        app.MapPost("/_emulator/reset", () =>
        {
            store.Reset();
            api.ResetThrottle();
            return Results.NoContent();
        });
        app.MapPost("/_emulator/faults", (FaultSettings f) =>
        {
            if (f.ThrottleLimit is { } limit) options.ThrottleLimit = limit;
            if (f.ThrottleWindowSeconds is { } window) options.ThrottleWindow = TimeSpan.FromSeconds(window);
            if (f.MinLatencyMs is { } min) options.MinLatency = TimeSpan.FromMilliseconds(min);
            if (f.MaxLatencyMs is { } max) options.MaxLatency = TimeSpan.FromMilliseconds(max);
            if (f.FailNextBatches is { } fail) options.FailNextBatches = fail;
            api.ResetThrottle();
            return Results.NoContent();
        });
        app.MapGet("/_emulator/stats", () => Results.Json(new JsonObject
        {
            ["served"] = api.Served,
            ["throttled"] = api.Throttled,
            ["tables"] = new JsonObject(store.Metadata.BySet.Keys.Select(s => KeyValuePair.Create(s, (JsonNode?)store.Count(s)))),
        }));
        app.MapGet("/", () => Results.Text("Dataverse Web API emulator for merchant-crm-bridge. Base URL: " + WebApi.Root));
        return app;
    }
}

public sealed record FaultSettings(int? ThrottleLimit, int? ThrottleWindowSeconds, int? MinLatencyMs, int? MaxLatencyMs, int? FailNextBatches);
