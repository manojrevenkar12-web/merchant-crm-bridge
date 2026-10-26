using System.Text.Json.Nodes;
using Bridge.Dataverse;

namespace Bridge.Tests;

/// <summary>The Web API rules the bridge relies on, exercised through the client against the emulator.</summary>
[Collection("emulator")]
public class DataverseClientTests(EmulatorFixture emulator) : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await emulator.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Create_then_read_by_alternate_key_with_an_etag()
    {
        var sales = emulator.Person();
        var created = await sales.CreateAsync("accounts", EmulatorFixture.Prospect("M-000001"), _ct);
        Assert.NotNull(created.Id);

        var (row, etag) = await sales.GetAsync("accounts", RowKey.Alternate(("mcb_merchantid", "M-000001")), "name,mcb_lifecyclestage", _ct);
        Assert.Equal("Kaffeerösterei Lindner GmbH", (string?)row!["name"]);
        Assert.Equal(771000000, (int?)row["mcb_lifecyclestage"]); // new accounts start as prospects
        Assert.StartsWith("W/\"", etag, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Patch_without_conditions_is_an_upsert_and_create_only_refuses_an_existing_row()
    {
        var bridge = emulator.Bridge();
        var key = RowKey.Alternate(("mcb_terminalref", "T-1"));
        await bridge.PatchAsync("mcb_terminals", key, new JsonObject { ["mcb_name"] = "T-1", ["mcb_model"] = "A920" }, null, null, _ct);
        await bridge.PatchAsync("mcb_terminals", key, new JsonObject { ["mcb_model"] = "A920 Pro" }, null, null, _ct);
        var page = await bridge.QueryAsync("mcb_terminals", null, "mcb_model", null, null, 10, _ct);
        Assert.Equal("A920 Pro", (string?)Assert.Single(page.Rows)["mcb_model"]);

        var ex = await Assert.ThrowsAsync<DataverseRequestException>(() =>
            bridge.PatchAsync("mcb_terminals", key, new JsonObject { ["mcb_model"] = "x" }, null, "*", _ct));
        Assert.Equal(412, ex.Status);
        Assert.Equal(DataverseRequestException.DuplicateKey, ex.Code);
    }

    [Fact]
    public async Task If_match_on_an_old_version_is_refused_with_the_concurrency_code()
    {
        var sales = emulator.Person();
        await sales.CreateAsync("accounts", EmulatorFixture.Prospect("M-000002"), _ct);
        var key = RowKey.Alternate(("mcb_merchantid", "M-000002"));
        var (_, etag) = await sales.GetAsync("accounts", key, "name", _ct);
        await sales.PatchAsync("accounts", key, new JsonObject { ["name"] = "Lindner Kaffee GmbH" }, etag, null, _ct);

        var ex = await Assert.ThrowsAsync<DataverseRequestException>(() =>
            sales.PatchAsync("accounts", key, new JsonObject { ["name"] = "again" }, etag, null, _ct));
        Assert.Equal(412, ex.Status);
        Assert.Equal(DataverseRequestException.ConcurrencyMismatch, ex.Code);
    }

    [Fact]
    public async Task A_changeset_commits_everything_or_nothing()
    {
        var bridge = emulator.Bridge();
        await bridge.PatchAsync("mcb_inboundevents", RowKey.Alternate(("mcb_eventid", "e-dup")), new JsonObject { ["mcb_name"] = "first" }, null, "*", _ct);

        var ops = new List<BatchOperation>
        {
            new("PATCH", "mcb_terminals(mcb_terminalref='T-9')", new JsonObject { ["mcb_name"] = "T-9" }),
            new("PATCH", "mcb_inboundevents(mcb_eventid='e-dup')", new JsonObject { ["mcb_name"] = "second" }, new Dictionary<string, string> { ["If-None-Match"] = "*" }),
        };
        var ex = await Assert.ThrowsAsync<DataverseRequestException>(() => bridge.ChangesetAsync(ops, _ct));
        Assert.Equal(DataverseRequestException.DuplicateKey, ex.Code);
        var terminals = await bridge.QueryAsync("mcb_terminals", null, "mcb_name", null, null, 10, _ct);
        Assert.Empty(terminals.Rows); // the terminal upsert before the failure was rolled back

        ops[1] = ops[1] with { RelativeUrl = "mcb_inboundevents(mcb_eventid='e-new')" };
        var results = await bridge.ChangesetAsync(ops, _ct);
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(204, r.Status));
    }

    [Fact]
    public async Task Filter_order_and_paging_follow_next_links()
    {
        var bridge = emulator.Bridge();
        for (var i = 0; i < 7; i++)
            await bridge.PatchAsync("mcb_terminals", RowKey.Alternate(("mcb_terminalref", $"T-{i}")), new JsonObject { ["mcb_name"] = $"T-{i}", ["mcb_merchantid"] = i % 2 == 0 ? "M-000010" : "M-000011" }, null, null, _ct);
        var first = await bridge.QueryAsync("mcb_terminals", "mcb_merchantid eq 'M-000010' or mcb_name eq 'T-1'", "mcb_name", "mcb_name desc", null, 3, _ct);
        Assert.Equal(["T-6", "T-4", "T-2"], first.Rows.Select(r => (string)r["mcb_name"]!));
        Assert.NotNull(first.NextLink);
        var second = await bridge.QueryUrlAsync(first.NextLink!, 3, _ct);
        Assert.Equal(["T-1", "T-0"], second.Rows.Select(r => (string)r["mcb_name"]!));
        Assert.Null(second.NextLink);
    }

    [Fact]
    public async Task People_cannot_write_platform_owned_columns()
    {
        var sales = emulator.Person();
        await sales.CreateAsync("accounts", EmulatorFixture.Prospect("M-000003"), _ct);
        var ex = await Assert.ThrowsAsync<DataverseRequestException>(() =>
            sales.PatchAsync("accounts", RowKey.Alternate(("mcb_merchantid", "M-000003")), new JsonObject { ["mcb_riskband"] = 771000400 }, "*", null, _ct));
        Assert.Equal(403, ex.Status);
        await emulator.Bridge().PatchAsync("accounts", RowKey.Alternate(("mcb_merchantid", "M-000003")), new JsonObject { ["mcb_riskband"] = 771000400 }, "*", null, _ct);
    }

    [Fact]
    public async Task Plug_in_rules_reject_a_bad_iban_before_anything_is_written()
    {
        var sales = emulator.Person();
        var bad = EmulatorFixture.Prospect("M-000004");
        bad["mcb_iban"] = "DE89370400440532013001";
        var ex = await Assert.ThrowsAsync<DataverseRequestException>(() => sales.CreateAsync("accounts", bad, _ct));
        Assert.Equal(DataverseRequestException.PluginAbort, ex.Code);
        Assert.Contains("check digits", ex.Detail, StringComparison.Ordinal);
        Assert.Empty((await sales.QueryAsync("accounts", null, "name", null, null, 10, _ct)).Rows);
    }

    [Fact]
    public async Task Onboarding_and_later_changes_write_outbox_rows_in_the_same_transaction()
    {
        var eventId = await emulator.OnboardAsync("M-000005");
        var sales = emulator.Person();
        var key = RowKey.Alternate(("mcb_merchantid", "M-000005"));
        await sales.PatchAsync("accounts", key, new JsonObject { ["mcb_pricingplan"] = 771000202 }, "*", null, _ct);

        var outbox = await emulator.Bridge().QueryAsync("mcb_outboxmessages", null, "mcb_eventid,mcb_eventtype,mcb_sequence,mcb_payload", "mcb_sequence asc", null, 10, _ct);
        Assert.Equal(2, outbox.Rows.Count);
        Assert.Equal(eventId, (string?)outbox.Rows[0]["mcb_eventid"]);
        Assert.Equal("crm.merchant.onboarding_requested", (string?)outbox.Rows[0]["mcb_eventtype"]);
        Assert.Equal(1L, (long?)outbox.Rows[0]["mcb_sequence"]);
        Assert.Equal("crm.merchant.details_changed", (string?)outbox.Rows[1]["mcb_eventtype"]);
        Assert.Equal(2L, (long?)outbox.Rows[1]["mcb_sequence"]);
        Assert.Contains("\"pricingPlan\":\"enterprise\"", (string?)outbox.Rows[1]["mcb_payload"], StringComparison.Ordinal);

        var (account, _) = await sales.GetAsync("accounts", key, "mcb_lifecyclestage,mcb_crmversion", _ct);
        Assert.Equal(771000001, (int?)account!["mcb_lifecyclestage"]);
        Assert.Equal(2L, (long?)account["mcb_crmversion"]);
    }

    [Fact]
    public async Task Short_throttling_is_waited_out_inline_and_long_throttling_is_handed_back()
    {
        emulator.Options.ThrottleLimit = 2;
        emulator.Options.ThrottleWindow = TimeSpan.FromSeconds(1);
        var bridge = emulator.Bridge();
        for (var i = 0; i < 4; i++) await bridge.QueryAsync("mcb_terminals", null, "mcb_name", null, null, 5, _ct); // waits ~1 s inline

        (await emulator.Http.PostAsync(new Uri(emulator.BaseUrl, "_emulator/faults"),
            new StringContent("{\"throttleLimit\":2,\"throttleWindowSeconds\":30}", System.Text.Encoding.UTF8, "application/json"), _ct)).EnsureSuccessStatusCode();
        var impatient = emulator.Bridge(options: new DataverseOptions { EnvironmentUrl = emulator.BaseUrl, MaxInlineRetryAfter = TimeSpan.FromSeconds(1) });
        await impatient.QueryAsync("mcb_terminals", null, "mcb_name", null, null, 5, _ct);
        await impatient.QueryAsync("mcb_terminals", null, "mcb_name", null, null, 5, _ct);
        var ex = await Assert.ThrowsAsync<DataverseRequestException>(() => impatient.QueryAsync("mcb_terminals", null, "mcb_name", null, null, 5, _ct));
        Assert.Equal(429, ex.Status);
        Assert.True(ex.IsTransient);
        Assert.InRange(ex.RetryAfter!.Value.TotalSeconds, 1, 30);
    }

    [Fact]
    public async Task Unknown_columns_and_bad_tokens_are_refused()
    {
        var bridge = emulator.Bridge();
        var ex = await Assert.ThrowsAsync<DataverseRequestException>(() => bridge.CreateAsync("mcb_terminals", new JsonObject { ["mcb_colour"] = "red" }, _ct));
        Assert.Equal(400, ex.Status);
        var stranger = new DataverseClient(emulator.Http, new StaticTokenProvider("not-a-token"), new DataverseOptions { EnvironmentUrl = emulator.BaseUrl }, TimeProvider.System);
        var unauthorized = await Assert.ThrowsAsync<DataverseRequestException>(() => stranger.QueryAsync("accounts", null, null, null, null, 5, _ct));
        Assert.Equal(401, unauthorized.Status);
    }

    [Fact]
    public async Task Client_credentials_token_from_the_emulator_authority()
    {
        using var provider = new ClientSecretTokenProvider(emulator.Http, emulator.BaseUrl, "tenant-1", "merchant-crm-bridge", "local-development-secret", TimeProvider.System);
        var client = new DataverseClient(emulator.Http, provider, new DataverseOptions { EnvironmentUrl = emulator.BaseUrl }, TimeProvider.System);
        await client.QueryAsync("mcb_terminals", null, null, null, null, 1, _ct);
        using var wrong = new ClientSecretTokenProvider(emulator.Http, emulator.BaseUrl, "tenant-1", "merchant-crm-bridge", "wrong", TimeProvider.System);
        await Assert.ThrowsAsync<DataverseRequestException>(() => wrong.GetTokenAsync("x/.default", _ct));
    }
}

internal static class JsonObjectExtensions
{
    public static JsonObject Let(this JsonObject o, Action<JsonObject> action)
    {
        action(o);
        return o;
    }
}
