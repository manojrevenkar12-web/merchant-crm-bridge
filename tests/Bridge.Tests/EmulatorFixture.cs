using System.Text.Json.Nodes;
using Bridge.Dataverse;
using Bridge.Emulator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Bridge.Tests;

/// <summary>A Dataverse emulator on a free local port, reset before each test that asks for it.</summary>
public sealed class EmulatorFixture : IAsyncLifetime
{
    private WebApplication? _app;

    public EmulatorOptions Options { get; } = new();
    public Uri BaseUrl { get; private set; } = null!;
    public HttpClient Http { get; } = new();
    public static readonly Guid SalesUser = Guid.Parse("5a1e5000-0000-4000-8000-000000000001");

    public async ValueTask InitializeAsync()
    {
        _app = EmulatorHost.Build(["--urls", "http://127.0.0.1:0"], Options);
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        BaseUrl = new Uri(address + "/");
        Options.BaseUrl = new Uri(BaseUrl, "api/data/v9.2/").ToString();
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        if (_app is not null) await _app.DisposeAsync();
    }

    public async Task ResetAsync()
    {
        Options.ThrottleLimit = 6000;
        Options.ThrottleWindow = TimeSpan.FromSeconds(300);
        Options.MinLatency = Options.MaxLatency = TimeSpan.Zero;
        Options.FailNextBatches = 0;
        (await Http.PostAsync(new Uri(BaseUrl, "_emulator/reset"), null)).EnsureSuccessStatusCode();
    }

    public DataverseClient Bridge(TimeProvider? clock = null, DataverseOptions? options = null) =>
        new(Http, new StaticTokenProvider("emu.app.merchant-crm-bridge"), options ?? new DataverseOptions { EnvironmentUrl = BaseUrl }, clock ?? TimeProvider.System);

    public DataverseClient Person(Guid? user = null, string name = "sales") =>
        new(Http, new StaticTokenProvider($"emu.user.{user ?? SalesUser}.{name}"), new DataverseOptions { EnvironmentUrl = BaseUrl }, TimeProvider.System);

    /// <summary>A prospect as sales would create it.</summary>
    public static JsonObject Prospect(string merchantId, string name = "Kaffeerösterei Lindner GmbH") => new()
    {
        ["name"] = name,
        ["emailaddress1"] = "kasse@lindner.example",
        ["mcb_merchantid"] = merchantId,
        ["mcb_legalform"] = 771000100,
        ["mcb_country"] = "DE",
        ["mcb_iban"] = "DE89370400440532013000",
        ["mcb_mcc"] = "5812",
        ["mcb_pricingplan"] = 771000201,
    };

    /// <summary>Creates a prospect as sales and requests onboarding; returns the outbox event id.</summary>
    public async Task<string> OnboardAsync(string merchantId)
    {
        var sales = Person();
        await sales.CreateAsync("accounts", Prospect(merchantId), CancellationToken.None);
        var result = await sales.InvokeBoundActionAsync("accounts", RowKey.Alternate(("mcb_merchantid", merchantId)), "mcb_RequestOnboarding", null, CancellationToken.None);
        return (string)result!["EventId"]!;
    }
}

[CollectionDefinition("emulator")]
public sealed class EmulatorCollection : ICollectionFixture<EmulatorFixture>;
