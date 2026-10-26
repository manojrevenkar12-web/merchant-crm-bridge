using Bridge.Core.Contracts;
using Bridge.Kafka;
using Bridge.PlatformSim;

// Standalone simulator for docker compose: Kafka from KAFKA_BOOTSTRAP, chaos switched by SIM_CHAOS.
var kafka = new KafkaOptions
{
    BootstrapServers = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP") ?? "localhost:9092",
    CreateTopics = true,
};
using var log = new KafkaEventLog(kafka);
await log.EnsureTopicsAsync([Topics.PlatformEvents, Topics.CrmEvents, Topics.PlatformEventsDlq], CancellationToken.None);
var chaos = Environment.GetEnvironmentVariable("SIM_CHAOS") == "off" ? ChaosOptions.None : new ChaosOptions();
var simulator = new Simulator(log, chaos, seed: 42, TimeProvider.System);
var app = PlatformHost.Build(args, log, simulator, tick: TimeSpan.FromSeconds(2),
    expectedToken: Environment.GetEnvironmentVariable("PLATFORM_TOKEN") ?? "emu.app.merchant-crm-bridge");
await app.RunAsync();
