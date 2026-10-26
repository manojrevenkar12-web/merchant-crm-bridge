using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Bridge.Api;
using Bridge.Core.Ports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Bridge.Api.Tests;

/// <summary>One API in demo mode for the whole collection: emulator, simulator and sales team running,
/// workers consuming. Waits until merchants have gone live so every view has data.</summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public WebApplicationFactory<BridgeSettings> Factory { get; } = new WebApplicationFactory<BridgeSettings>().WithWebHostBuilder(b =>
    {
        b.UseEnvironment("Development");
        b.UseSetting("Bridge:Demo", "true");
    });

    public HttpClient Operator { get; private set; } = null!;
    public HttpClient Reader { get; private set; } = null!;
    public HttpClient Anonymous { get; private set; } = null!;
    public IEventLog Log => Factory.Services.GetRequiredService<IEventLog>();
    public BridgeSettings Settings => Factory.Services.GetRequiredService<BridgeSettings>();

    public HttpClient With(string? token)
    {
        var c = Factory.CreateClient();
        if (token is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public string Token(params string[] roles) => Auth.IssueLocalToken(Settings.Auth, "Test Operator", roles, TimeProvider.System);

    public async ValueTask InitializeAsync()
    {
        Anonymous = Factory.CreateClient();
        Operator = With(Token(Auth.Read, Auth.Operate));
        Reader = With(Token(Auth.Read));
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            var overview = await Operator.GetFromJsonAsync<JsonElement>("/v1/overview");
            var live = overview.GetProperty("stages").EnumerateArray().Where(s => s.GetProperty("stage").GetString() == "live").Sum(s => s.GetProperty("count").GetInt32());
            if (live >= 5) return;
            await Task.Delay(500);
        }
        throw new TimeoutException("the demo world did not bring five merchants live in 90 s");
    }

    public async ValueTask DisposeAsync()
    {
        Operator?.Dispose();
        Reader?.Dispose();
        Anonymous?.Dispose();
        await Factory.DisposeAsync();
    }
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>;
