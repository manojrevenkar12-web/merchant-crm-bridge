using Bridge.Hosting;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

// App settings use Bridge__Dataverse__EnvironmentUrl and so on; secrets are Key Vault references
// (@Microsoft.KeyVault(SecretUri=...)) resolved by the platform with the app's managed identity.
var settings = builder.Configuration.GetSection("Bridge").Get<BridgeSettings>() ?? new BridgeSettings();
builder.Services.AddBridgeCore(settings);
builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

builder.Build().Run();
