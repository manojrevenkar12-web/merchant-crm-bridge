using Bridge.Hosting;

namespace Bridge.Api;

/// <summary>The bridge's services plus what only the App Service host has: read models for operations and,
/// when RunWorkers is on (container hosting), the consumers, relay and reconciler as hosted services.</summary>
public static class Composition
{
    public static IServiceCollection AddBridge(this IServiceCollection services, BridgeSettings settings)
    {
        services.AddBridgeCore(settings);
        services.AddSingleton<OpsQueries>();
        services.AddSingleton<WorkerState>();
        if (settings.RunWorkers) services.AddHostedService<Workers>();
        return services;
    }
}
