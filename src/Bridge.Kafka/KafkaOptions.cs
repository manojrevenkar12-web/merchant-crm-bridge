using Confluent.Kafka;

namespace Bridge.Kafka;

public sealed class KafkaOptions
{
    public string BootstrapServers { get; set; } = "localhost:9092";

    /// <summary>PLAINTEXT locally; SASL_SSL against Confluent Cloud or Azure Event Hubs (Kafka endpoint).</summary>
    public string SecurityProtocol { get; set; } = "Plaintext";
    public string? SaslMechanism { get; set; }
    public string? SaslUsername { get; set; }

    /// <summary>From Key Vault (a Key Vault reference in app settings), never from source.</summary>
    public string? SaslPassword { get; set; }

    public int Partitions { get; set; } = 6;
    public short ReplicationFactor { get; set; } = 1;
    public bool CreateTopics { get; set; }

    internal void Apply(ClientConfig config)
    {
        config.BootstrapServers = BootstrapServers;
        config.SecurityProtocol = Enum.Parse<SecurityProtocol>(SecurityProtocol, ignoreCase: true);
        if (SaslMechanism is not null)
        {
            config.SaslMechanism = Enum.Parse<SaslMechanism>(SaslMechanism, ignoreCase: true);
            config.SaslUsername = SaslUsername;
            config.SaslPassword = SaslPassword;
        }
    }
}
