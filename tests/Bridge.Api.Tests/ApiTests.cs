using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Bridge.Core.Contracts;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Bridge.Api.Tests;

[Collection("api")]
public sealed class ContractTests(ApiFixture api)
{
    private static readonly string[] NotInContract = ["/openapi.json", "/dev/token", "/graphql", "{*path:nonfile}"];

    [Fact]
    public void Every_mapped_endpoint_is_in_the_contract_and_every_contract_operation_is_mapped()
    {
        var mapped = api.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => !NotInContract.Any(n => e.RoutePattern.RawText!.Contains(n, StringComparison.Ordinal)))
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => (m, Normalise(e.RoutePattern.RawText!))))
            .ToHashSet();
        var contract = OpenApi.Default.Operations.ToHashSet();
        Assert.Empty(mapped.Except(contract));
        Assert.Empty(contract.Except(mapped));
    }

    private static string Normalise(string route) =>
        "/" + System.Text.RegularExpressions.Regex.Replace(route.TrimStart('/'), @"\{(\w+):\w+\}", "{$1}");

    [Fact]
    public async Task Read_responses_match_the_contract()
    {
        var merchant = await LiveMerchantAsync();
        foreach (var (method, path, url) in new[]
        {
            ("GET", "/healthz", "/healthz"),
            ("GET", "/v1/overview", "/v1/overview"),
            ("GET", "/v1/merchants", "/v1/merchants"),
            ("GET", "/v1/merchants", "/v1/merchants?stage=live&top=5"),
            ("GET", "/v1/merchants", "/v1/merchants?stage=sleeping"),
            ("GET", "/v1/merchants/{merchantId}", $"/v1/merchants/{merchant}"),
            ("GET", "/v1/merchants/{merchantId}", "/v1/merchants/M-999999"),
            ("GET", "/v1/merchants/{merchantId}", "/v1/merchants/42"),
            ("GET", "/v1/dead-letters", "/v1/dead-letters"),
            ("GET", "/v1/drift", "/v1/drift"),
        })
        {
            await AssertMatchesAsync(api.Operator, method, path, url);
        }
    }

    [Fact]
    public async Task Command_responses_match_the_contract()
    {
        await AssertMatchesAsync(api.Operator, "POST", "/v1/reconcile", "/v1/reconcile");
        await AssertMatchesAsync(api.Operator, "POST", "/v1/dead-letters/{partition}/{offset}/replay", "/v1/dead-letters/0/999999/replay");
    }

    internal static async Task<HttpResponseMessage> AssertMatchesAsync(HttpClient client, string method, string path, string url)
    {
        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));
        var body = await response.Content.ReadAsStringAsync();
        var errors = OpenApi.Default.Check(method, path, (int)response.StatusCode, response.Content.Headers.ContentType?.ToString() ?? "", body);
        Assert.True(errors.Count == 0, $"{method} {url} -> {(int)response.StatusCode}: {string.Join("; ", errors)}\n{body[..Math.Min(body.Length, 600)]}");
        return response;
    }

    private async Task<string> LiveMerchantAsync()
    {
        var list = await api.Operator.GetFromJsonAsync<JsonElement>("/v1/merchants?stage=live&top=1");
        return list[0].GetProperty("merchantId").GetString()!;
    }
}

[Collection("api")]
public sealed class SecurityTests(ApiFixture api)
{
    [Theory]
    [InlineData("/v1/overview")]
    [InlineData("/v1/merchants")]
    [InlineData("/v1/dead-letters")]
    [InlineData("/v1/drift")]
    public async Task Reads_need_a_token(string url)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Anonymous.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Reader.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Commands_need_the_operate_role()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Reader.PostAsync("/v1/reconcile", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Reader.PostAsync("/v1/dead-letters/0/0/replay", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Anonymous.PostAsync("/v1/reconcile", null)).StatusCode);
    }

    [Fact]
    public async Task A_token_without_roles_reads_nothing()
    {
        using var none = api.With(api.Token());
        Assert.Equal(HttpStatusCode.Forbidden, (await none.GetAsync("/v1/overview")).StatusCode);
    }

    [Fact]
    public async Task Tokens_signed_with_another_key_or_for_another_audience_are_refused()
    {
        var forged = Auth.IssueLocalToken(new AuthSettings { LocalSigningKey = "someone-elses-signing-key-0123456789-abcdefghijkl" }, "Mallory", [Auth.Operate], TimeProvider.System);
        using var a = api.With(forged);
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.GetAsync("/v1/overview")).StatusCode);

        var otherAudience = Auth.IssueLocalToken(new AuthSettings { Audience = "api://another-app", LocalSigningKey = api.Settings.Auth.LocalSigningKey }, "Mallory", [Auth.Operate], TimeProvider.System);
        using var b = api.With(otherAudience);
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.GetAsync("/v1/overview")).StatusCode);
    }

    [Fact]
    public async Task Expired_tokens_are_refused()
    {
        var past = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(-2));
        using var c = api.With(Auth.IssueLocalToken(api.Settings.Auth, "Yesterday", [Auth.Read], past));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/v1/overview")).StatusCode);
    }

    [Fact]
    public void Local_tokens_are_refused_outside_development_and_demo()
    {
        var env = new TestEnvironment("Production");
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddBridgeAuth(new AuthSettings { Mode = "Local" }, env, demo: false));
        services.AddBridgeAuth(new AuthSettings { Mode = "Entra", TenantId = "00000000-0000-0000-0000-000000000001" }, env, demo: false);
    }

    [Fact]
    public async Task Operations_see_a_masked_iban_only()
    {
        var list = await api.Operator.GetFromJsonAsync<JsonElement>("/v1/merchants?top=50");
        foreach (var m in list.EnumerateArray().Take(10))
        {
            var id = m.GetProperty("merchantId").GetString()!;
            var body = await api.Operator.GetStringAsync($"/v1/merchants/{id}");
            Assert.DoesNotMatch(@"DE\d{20}", body);
            var masked = JsonDocument.Parse(body).RootElement.GetProperty("ibanMasked").GetString();
            Assert.Matches(@"^DE\d{2} •••• \d{4}$", masked);
        }
    }

    [Fact]
    public async Task Search_text_cannot_change_the_query()
    {
        var response = await api.Operator.GetAsync("/v1/merchants?search=" + Uri.EscapeDataString("x') or (mcb_merchantid ne null"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());
    }

    private sealed class TestEnvironment(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Bridge.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

[Collection("api")]
public sealed class DeadLetterTests(ApiFixture api)
{
    [Fact]
    public async Task A_broken_event_is_dead_lettered_replayed_once_and_traced()
    {
        var list = await api.Operator.GetFromJsonAsync<JsonElement>("/v1/merchants?stage=live&top=1");
        var merchant = list[0].GetProperty("merchantId").GetString()!;
        var id = Guid.NewGuid().ToString();
        var broken = $$$"""{"specversion":"1.0","id":"{{{id}}}","source":"payments-platform://tests","type":"platform.merchant.risk_changed","subject":"{{{merchant}}}","time":"2026-10-26T10:00:00Z","sequence":"1","schemaversion":"1","datacontenttype":"application/json","data":{"riskBand":"purple"}}""";
        await api.Log.ProduceAsync(Topics.PlatformEvents, merchant, broken, null, TestContext.Current.CancellationToken);

        JsonElement letter = default;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var letters = await api.Operator.GetFromJsonAsync<JsonElement>("/v1/dead-letters");
            var found = letters.EnumerateArray().Where(l => l.GetProperty("eventId").GetString() == id).ToList();
            if (found.Count > 0) { letter = found[0]; break; }
            await Task.Delay(250);
        }
        Assert.NotEqual(JsonValueKind.Undefined, letter.ValueKind);
        Assert.Equal("contract", letter.GetProperty("reason").GetString());
        Assert.False(letter.GetProperty("replayed").GetBoolean());

        var url = $"/v1/dead-letters/{letter.GetProperty("partition").GetInt32()}/{letter.GetProperty("offset").GetInt64()}/replay";
        var first = await ContractTests.AssertMatchesAsync(api.Operator, "POST", "/v1/dead-letters/{partition}/{offset}/replay", url);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var second = await ContractTests.AssertMatchesAsync(api.Operator, "POST", "/v1/dead-letters/{partition}/{offset}/replay", url);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var platform = await api.Log.ReadTopicAsync(Topics.PlatformEvents, 100_000, TestContext.Current.CancellationToken);
        var replayed = Assert.Single(platform, m => m.Value.Contains(id, StringComparison.Ordinal) && m.Headers.ContainsKey("replay-of"));
        Assert.Equal("Test Operator", replayed.Headers["replay-by"]);
    }
}

[Collection("api")]
public sealed class GraphQlTests(ApiFixture api)
{
    private async Task<JsonElement> QueryAsync(HttpClient client, string query)
    {
        var response = await client.PostAsync("/graphql", new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json"));
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task A_merchant_in_one_request_matches_rest()
    {
        var list = await api.Operator.GetFromJsonAsync<JsonElement>("/v1/merchants?stage=live&top=1");
        var id = list[0].GetProperty("merchantId").GetString()!;
        var rest = await api.Operator.GetFromJsonAsync<JsonElement>($"/v1/merchants/{id}");
        var gql = await QueryAsync(api.Reader, $$"""{ merchant(id: "{{id}}") { summary { merchantId legalName } ibanMasked aspects { aspect inSync } } }""");
        var m = gql.GetProperty("data").GetProperty("merchant");
        Assert.Equal(id, m.GetProperty("summary").GetProperty("merchantId").GetString());
        Assert.Equal(rest.GetProperty("ibanMasked").GetString(), m.GetProperty("ibanMasked").GetString());
        Assert.Equal(rest.GetProperty("aspects").GetArrayLength(), m.GetProperty("aspects").GetArrayLength());
    }

    [Fact]
    public async Task Invalid_ids_are_refused_and_anonymous_callers_get_nothing()
    {
        var bad = await QueryAsync(api.Reader, """{ merchant(id: "x' or 1 eq 1") { ibanMasked } }""");
        Assert.Equal("INVALID_MERCHANT_ID", bad.GetProperty("errors")[0].GetProperty("extensions").GetProperty("code").GetString());

        var anonymous = await api.Anonymous.PostAsync("/graphql", new StringContent("""{"query":"{ overview { merchants } }"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
