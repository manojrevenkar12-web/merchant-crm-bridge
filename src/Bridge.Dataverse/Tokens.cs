using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Bridge.Dataverse;

/// <summary>Access tokens for a resource (scope "{environment}/.default").</summary>
public interface ITokenProvider
{
    Task<string> GetTokenAsync(string scope, CancellationToken ct);
}

/// <summary>A fixed token: tests, and the emulator's person tokens.</summary>
public sealed class StaticTokenProvider(string token) : ITokenProvider
{
    public Task<string> GetTokenAsync(string scope, CancellationToken ct) => Task.FromResult(token);
}

internal sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

/// <summary>Base class that caches a token until five minutes before it expires.</summary>
public abstract class CachingTokenProvider(TimeProvider clock) : ITokenProvider, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (string Token, DateTimeOffset Expires)> _cache = new(StringComparer.Ordinal);

    public async Task<string> GetTokenAsync(string scope, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(scope, out var cached) && cached.Expires - TimeSpan.FromMinutes(5) > clock.GetUtcNow()) return cached.Token;
            var (token, expiresIn) = await AcquireAsync(scope, ct).ConfigureAwait(false);
            _cache[scope] = (token, clock.GetUtcNow() + expiresIn);
            return token;
        }
        finally
        {
            _gate.Release();
        }
    }

    protected abstract Task<(string Token, TimeSpan ExpiresIn)> AcquireAsync(string scope, CancellationToken ct);

    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>OAuth 2.0 client credentials against Microsoft Entra ID (an app registration with a secret
/// from Key Vault). Production uses a managed identity instead; this is for local and CI.</summary>
public sealed class ClientSecretTokenProvider(HttpClient http, Uri authority, string tenantId, string clientId, string clientSecret, TimeProvider clock)
    : CachingTokenProvider(clock)
{
    protected override async Task<(string, TimeSpan)> AcquireAsync(string scope, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = scope,
        });
        using var response = await http.PostAsync(new Uri(authority, $"{tenantId}/oauth2/v2.0/token"), form, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new DataverseRequestException((int)response.StatusCode, "token", $"token request failed: {await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)}");
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>(ct).ConfigureAwait(false)
            ?? throw new DataverseRequestException(500, "token", "empty token response");
        return (body.AccessToken, TimeSpan.FromSeconds(body.ExpiresIn));
    }
}

/// <summary>The managed identity of an App Service or Function App (IDENTITY_ENDPOINT/IDENTITY_HEADER):
/// no secret exists anywhere.</summary>
public sealed class ManagedIdentityTokenProvider(HttpClient http, TimeProvider clock, string? clientId = null) : CachingTokenProvider(clock)
{
    protected override async Task<(string, TimeSpan)> AcquireAsync(string scope, CancellationToken ct)
    {
        var endpoint = Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT")
            ?? throw new InvalidOperationException("IDENTITY_ENDPOINT is not set: no managed identity in this environment.");
        var header = Environment.GetEnvironmentVariable("IDENTITY_HEADER") ?? "";
        var resource = scope.EndsWith("/.default", StringComparison.Ordinal) ? scope[..^"/.default".Length] : scope;
        var url = $"{endpoint}?api-version=2019-08-01&resource={Uri.EscapeDataString(resource)}" + (clientId is null ? "" : $"&client_id={clientId}");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-IDENTITY-HEADER", header);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ManagedIdentityResponse>(ct).ConfigureAwait(false)
            ?? throw new DataverseRequestException(500, "token", "empty managed identity response");
        var expiresOn = DateTimeOffset.FromUnixTimeSeconds(long.Parse(body.ExpiresOn, System.Globalization.CultureInfo.InvariantCulture));
        return (body.AccessToken, expiresOn - DateTimeOffset.UtcNow);
    }

    private sealed record ManagedIdentityResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_on")] string ExpiresOn);
}
