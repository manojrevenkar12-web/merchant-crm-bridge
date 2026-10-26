using Azure.Core;
using Azure.Identity;
using Bridge.Core.Contracts;
using Bridge.Core.Messaging;
using Bridge.Core.Observability;
using Bridge.Core.Ports;
using Bridge.Core.Services;
using Bridge.Dataverse;
using Bridge.Kafka;
using Bridge.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bridge.Hosting;

/// <summary>Wires the bridge from settings: the same services run in the App Service and in the Function App.</summary>
public static class BridgeServices
{
    public static IServiceCollection AddBridgeCore(this IServiceCollection services, BridgeSettings settings)
    {
        services.AddSingleton(settings);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<BridgeTelemetry>();
        services.AddHttpClient("dataverse", c => c.Timeout = TimeSpan.FromSeconds(100));
        services.AddHttpClient("platform", c => c.Timeout = TimeSpan.FromSeconds(30));

        services.AddSingleton<ITokenProvider>(sp => Credential(settings.Dataverse, sp));
        services.AddSingleton(sp => new DataverseClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("dataverse"),
            sp.GetRequiredService<ITokenProvider>(),
            new DataverseOptions { EnvironmentUrl = settings.Dataverse.EnvironmentUrl },
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ICrmGateway>(sp => new DataverseCrmGateway(sp.GetRequiredService<DataverseClient>(), sp.GetRequiredService<TimeProvider>()));

        if (string.IsNullOrWhiteSpace(settings.Kafka.BootstrapServers))
        {
            services.TryAddSingleton<IEventLog>(new InMemoryEventLog(partitions: 4));
        }
        else
        {
            services.TryAddSingleton<IEventLog>(_ => new KafkaEventLog(new KafkaOptions
            {
                BootstrapServers = settings.Kafka.BootstrapServers,
                SecurityProtocol = settings.Kafka.SecurityProtocol,
                SaslMechanism = settings.Kafka.SaslMechanism,
                SaslUsername = settings.Kafka.SaslUsername,
                SaslPassword = settings.Kafka.SaslPassword,
            }));
        }

        services.AddSingleton<IPlatformApi>(sp => new PlatformGraphQlClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("platform"),
            sp.GetRequiredService<ITokenProvider>(),
            new PlatformOptions { GraphQlEndpoint = settings.Platform.GraphQlEndpoint, Scope = settings.Platform.Scope }));

        services.AddSingleton(SchemaRegistry.Default);
        services.AddSingleton(EventReader.Default);
        services.AddSingleton(sp => new PlatformEventApplier(sp.GetRequiredService<ICrmGateway>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<BridgeTelemetry>()));
        services.AddSingleton(new ConsumerOptions { GroupId = settings.Kafka.GroupId });
        services.AddSingleton<PlatformConsumer>();
        services.AddSingleton(new RelayOptions { Source = "dynamics365://" + settings.Dataverse.EnvironmentUrl.Host });
        services.AddSingleton<OutboxRelay>();
        services.AddSingleton(new ReconcilerOptions());
        services.AddSingleton<Reconciler>();
        return services;
    }

    private static ITokenProvider Credential(DataverseSettings s, IServiceProvider sp) => s.Credential switch
    {
        "Static" => new StaticTokenProvider(s.StaticToken),
        "ClientSecret" => new ClientSecretTokenProvider(sp.GetRequiredService<IHttpClientFactory>().CreateClient("dataverse"), s.Authority,
            s.TenantId ?? throw new InvalidOperationException("Dataverse:TenantId is required"),
            s.ClientId ?? throw new InvalidOperationException("Dataverse:ClientId is required"),
            s.ClientSecret ?? throw new InvalidOperationException("Dataverse:ClientSecret is required (Key Vault reference)"),
            sp.GetRequiredService<TimeProvider>()),
        _ => new AzureCredentialTokenProvider(new ManagedIdentityCredential(s.ClientId is null ? ManagedIdentityId.SystemAssigned : ManagedIdentityId.FromUserAssignedClientId(s.ClientId))),
    };
}

/// <summary>Tokens from Azure.Identity (managed identity in Azure); the SDK caches and refreshes them.</summary>
public sealed class AzureCredentialTokenProvider(TokenCredential credential) : ITokenProvider
{
    public async Task<string> GetTokenAsync(string scope, CancellationToken ct) =>
        (await credential.GetTokenAsync(new TokenRequestContext([scope]), ct).ConfigureAwait(false)).Token;
}
