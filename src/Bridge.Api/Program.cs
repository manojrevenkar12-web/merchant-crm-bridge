using Bridge.Hosting;
using Bridge.Api;
using Bridge.Core.Ports;

// The container health check: the image has no curl, so the app checks itself.
if (args.Contains("--healthcheck"))
{
    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    try { return (await probe.GetAsync(new Uri("http://127.0.0.1:8080/healthz"))).IsSuccessStatusCode ? 0 : 1; }
    catch (HttpRequestException) { return 1; }
    catch (TaskCanceledException) { return 1; }
}

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("Bridge").Get<BridgeSettings>() ?? new BridgeSettings();

if (settings.Demo)
{
    var demo = DemoWorld.Configure(settings);
    builder.Services.AddSingleton(demo);
    builder.Services.AddHostedService(sp => sp.GetRequiredService<DemoWorld>()); // before the workers, so they find it running
    builder.Services.AddSingleton<IEventLog>(demo.Log);
}

builder.Services.AddBridge(settings);
builder.Services.AddBridgeAuth(settings.Auth, builder.Environment, settings.Demo);
builder.Services.AddProblemDetails();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(settings.Auth.CorsOrigins).AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddGraphQLServer().AddQueryType<BridgeQuery>();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapBridgeApi();
app.MapGraphQL().RequireAuthorization(Auth.Read);

if (settings.Auth.Mode.Equals("Local", StringComparison.OrdinalIgnoreCase))
{
    // Development tokens with the same app roles Entra issues. Refused at startup outside Development/demo.
    app.MapPost("/dev/token", (DevTokenRequest request, TimeProvider clock) =>
        Results.Ok(new { access_token = Auth.IssueLocalToken(settings.Auth, request.Name ?? "Operations", request.Roles ?? [Auth.Read, Auth.Operate], clock) }))
        .AllowAnonymous();
}
app.MapFallbackToFile("index.html");

await app.RunAsync();
return 0;

internal sealed record DevTokenRequest(string? Name, string[]? Roles);


