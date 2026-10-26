using Bridge.Core.Contracts;
using Bridge.Core.Messaging;
using Bridge.Core.Ports;
using Bridge.Dataverse;
using Bridge.Emulator;
using Bridge.PlatformSim;

namespace Bridge.Api;

/// <summary>
/// Demo mode: a Dataverse emulator, the platform simulator with some chaos, an in-memory log and a sales
/// team, all in this process, so the API and the operations console show a living system on a laptop.
/// Never used with real data: Auth:Mode=Local is forced and the emulator holds only invented merchants.
/// </summary>
public sealed class DemoWorld : IHostedService, IAsyncDisposable
{
    private static readonly string[] SalesPeople = ["Lea Hoffmann", "Jonas Weber", "Mira Schulz"];
    private static readonly string[] Mccs = ["5812", "5411", "7230", "5732", "5945"];

    private readonly Uri _emulatorUrl;
    private readonly Uri _platformUrl;
    private readonly CancellationTokenSource _stop = new();
    private WebApplication? _emulator;
    private WebApplication? _platform;
    private Task? _sales;

    public InMemoryEventLog Log { get; } = new(partitions: 4);
    public Simulator Simulator { get; }

    private DemoWorld(Uri emulatorUrl, Uri platformUrl)
    {
        _emulatorUrl = emulatorUrl;
        _platformUrl = platformUrl;
        var chaos = new ChaosOptions { Duplicate = 0.08, Reorder = 0.08, Drop = 0.02, OldVolumeSchema = 0.3, PoisonPerHundredTicks = 2 };
        Simulator = new Simulator(Log, chaos, seed: 2026, TimeProvider.System);
    }

    /// <summary>Points the settings at the demo world. The hosts start with the application (StartAsync), so
    /// the API's own host is the first one built in the process.</summary>
    public static DemoWorld Configure(BridgeSettings settings)
    {
        var world = new DemoWorld(new Uri($"http://127.0.0.1:{FreePort()}/"), new Uri($"http://127.0.0.1:{FreePort()}/"));
        settings.Dataverse.EnvironmentUrl = world._emulatorUrl;
        settings.Dataverse.Credential = "Static";
        settings.Platform.GraphQlEndpoint = new Uri(world._platformUrl, "graphql");
        settings.Kafka.BootstrapServers = null;
        settings.Auth.Mode = "Local";
        settings.RelayInterval = TimeSpan.FromSeconds(1);
        settings.ReconcileInterval = TimeSpan.FromSeconds(45);
        return world;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var emulatorOptions = new EmulatorOptions { MaxLatency = TimeSpan.FromMilliseconds(15), BaseUrl = new Uri(_emulatorUrl, "api/data/v9.2/").ToString() };
        _emulator = EmulatorHost.Build(["--urls", _emulatorUrl.ToString().TrimEnd('/')], emulatorOptions);
        await _emulator.StartAsync(cancellationToken);
        _platform = PlatformHost.Build(["--urls", _platformUrl.ToString().TrimEnd('/')], Log, Simulator, tick: TimeSpan.FromMilliseconds(1200));
        await _platform.StartAsync(cancellationToken);
        _sales = Task.Run(() => SalesAsync(_emulatorUrl, _stop.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stop.CancelAsync();
        if (_sales is not null) { try { await _sales; } catch (OperationCanceledException) { } }
        if (_platform is not null) await _platform.StopAsync(cancellationToken);
        if (_emulator is not null) await _emulator.StopAsync(cancellationToken);
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    // Sales onboards merchants in waves and edits some now and then.
    private static async Task SalesAsync(Uri emulatorUrl, CancellationToken ct)
    {
        using var http = new HttpClient();
        var people = SalesPeople.Select((name, i) =>
            new DataverseClient(http, new StaticTokenProvider($"emu.user.{new Guid(i + 1, 0, 0, new byte[8])}.{name.Replace(' ', '_')}"),
                new DataverseOptions { EnvironmentUrl = emulatorUrl }, TimeProvider.System)).ToList();
        var random = new Random(7);
        var names = new[] { "Kaffeerösterei", "Fahrradladen", "Buchhandlung", "Feinkost", "Blumenwerkstatt", "Optik", "Weinhandlung", "Bäckerei", "Friseursalon", "Spielwaren", "Eisdiele", "Schuhhaus" };
        var cities = new[] { "Berlin", "Heidelberg", "Leipzig", "Köln", "Hamburg", "München", "Mannheim", "Freiburg" };
        var onboarded = new List<string>();
        var n = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (onboarded.Count < 36)
                {
                    for (var i = 0; i < 3; i++)
                    {
                        n++;
                        var id = $"M-{4100 + n:000000}";
                        var person = people[random.Next(people.Count)];
                        await person.CreateAsync("accounts", new System.Text.Json.Nodes.JsonObject
                        {
                            ["name"] = $"{names[n % names.Length]} {cities[n % cities.Length]} GmbH",
                            ["emailaddress1"] = $"kasse@{names[n % names.Length].ToLowerInvariant()}-{n}.example",
                            ["mcb_merchantid"] = id,
                            ["mcb_legalform"] = 771000100 + (n % 3 == 0 ? 3 : 0),
                            ["mcb_country"] = "DE",
                            ["mcb_iban"] = Iban(n),
                            ["mcb_mcc"] = n % 13 == 0 ? "7995" : n % 5 == 0 ? "5993" : Mccs[n % 5],
                            ["mcb_pricingplan"] = 771000200 + n % 3,
                        }, ct);
                        if (n % 9 != 0) // some stay prospects for a while
                        {
                            await person.InvokeBoundActionAsync("accounts", RowKey.Alternate(("mcb_merchantid", id)), "mcb_RequestOnboarding", null, ct);
                            onboarded.Add(id);
                        }
                    }
                }
                else if (random.NextDouble() < 0.5)
                {
                    var id = onboarded[random.Next(onboarded.Count)];
                    await people[random.Next(people.Count)].PatchAsync("accounts", RowKey.Alternate(("mcb_merchantid", id)),
                        new System.Text.Json.Nodes.JsonObject { ["mcb_pricingplan"] = 771000200 + random.Next(3) }, "*", null, ct);
                }
            }
            catch (DataverseRequestException) { /* rule refused (e.g. a rejected merchant): people move on */ }
            catch (OperationCanceledException) { return; }
            try { await Task.Delay(onboarded.Count < 36 ? 1500 : 5000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private static string Iban(int n)
    {
        var bban = "37040044" + (5_320_130_000L + n).ToString("0000000000", System.Globalization.CultureInfo.InvariantCulture);
        var check = 98 - (int)(System.Numerics.BigInteger.Parse(bban + "131400", System.Globalization.CultureInfo.InvariantCulture) % 97);
        return "DE" + check.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + bban;
    }

    public async ValueTask DisposeAsync()
    {
        if (_platform is not null) await _platform.DisposeAsync();
        if (_emulator is not null) await _emulator.DisposeAsync();
        _stop.Dispose();
    }
}
