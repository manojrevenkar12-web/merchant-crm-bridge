namespace Bridge.Hosting;

/// <summary>Configuration (appsettings, environment variables, Key Vault references in Azure).</summary>
public sealed class BridgeSettings
{
    /// <summary>Starts a Dataverse emulator, a platform simulator, an in-memory log and a sales team in
    /// process: the whole system on one machine without a tenant, Kafka or Docker.</summary>
    public bool Demo { get; set; }

    /// <summary>Run consumers, relay and reconciler here (container hosting). In Azure the Function App runs them.</summary>
    public bool RunWorkers { get; set; } = true;

    public int Consumers { get; set; } = 2;
    public TimeSpan RelayInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Function App timers (NCRONTAB with seconds); the App Service host uses the intervals above.</summary>
    public string RelaySchedule { get; set; } = "*/5 * * * * *";
    public string ReconcileSchedule { get; set; } = "0 */15 * * * *";

    public DataverseSettings Dataverse { get; set; } = new();
    public KafkaSettings Kafka { get; set; } = new();
    public PlatformSettings Platform { get; set; } = new();
    public AuthSettings Auth { get; set; } = new();
}

public sealed class DataverseSettings
{
    public Uri EnvironmentUrl { get; set; } = new("http://localhost:5100/");

    /// <summary>ManagedIdentity (Azure), ClientSecret (an app registration; secret from Key Vault) or Static (emulator).</summary>
    public string Credential { get; set; } = "ManagedIdentity";

    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public Uri Authority { get; set; } = new("https://login.microsoftonline.com/");
    public string StaticToken { get; set; } = "emu.app.merchant-crm-bridge";
}

public sealed class KafkaSettings
{
    /// <summary>Empty: an in-memory log (demo and tests).</summary>
    public string? BootstrapServers { get; set; }
    public string SecurityProtocol { get; set; } = "Plaintext";
    public string? SaslMechanism { get; set; }
    public string? SaslUsername { get; set; }
    public string? SaslPassword { get; set; }
    public string GroupId { get; set; } = "merchant-crm-bridge";
}

public sealed class PlatformSettings
{
    public Uri GraphQlEndpoint { get; set; } = new("http://localhost:5200/graphql");
    public string Scope { get; set; } = "api://payments-platform/.default";
}

public sealed class AuthSettings
{
    /// <summary>Entra (production) or Local (development tokens from /dev/token; refused outside Development and demo).</summary>
    public string Mode { get; set; } = "Entra";

    public string? TenantId { get; set; }

    /// <summary>The API's application ID URI, e.g. api://merchant-crm-bridge.</summary>
    public string Audience { get; set; } = "api://merchant-crm-bridge";

    public string LocalSigningKey { get; set; } = "local-development-only-signing-key-change-me-0123456789";

    public string[] CorsOrigins { get; set; } = [];
}
