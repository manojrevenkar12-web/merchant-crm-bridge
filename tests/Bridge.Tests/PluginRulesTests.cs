using MerchantCrmBridge.Plugins.Logic;

namespace Bridge.Tests;

public class PluginRulesTests
{
    [Theory]
    [InlineData("DE89 3704 0044 0532 0130 00", null)]
    [InlineData("de89370400440532013000", null)]
    [InlineData("AT611904300234573201", null)]
    [InlineData("NL91ABNA0417164300", null)]
    [InlineData("DE89370400440532013001", "IBAN check digits are wrong.")]
    [InlineData("DE8937040044053201300", "IBAN for DE must have 22 characters.")]
    [InlineData("US89370400440532013000", "IBAN country US is not a settlement country.")]
    [InlineData("", "IBAN is missing.")]
    public void Iban_is_checked_by_country_length_and_mod_97(string iban, string? expected) =>
        Assert.Equal(expected, Rules.CheckIban(iban));

    [Theory]
    [InlineData("M-000123", null)]
    [InlineData("M-12345", "Merchant ID must look like M-000123.")]
    [InlineData("X-000123", "Merchant ID must look like M-000123.")]
    public void Merchant_id_format(string id, string? expected) => Assert.Equal(expected, Rules.CheckMerchantId(id));

    [Theory]
    [InlineData("anna@shop.example", null)]
    [InlineData("anna@shop", "Contact email is not valid.")]
    [InlineData("anna@@shop.example", "Contact email is not valid.")]
    public void Email(string email, string? expected) => Assert.Equal(expected, Rules.CheckEmail(email));

    private static Rec Account(int stage, string merchantId = "M-000001") => new()
    {
        [Model.MerchantId] = merchantId,
        [Model.LifecycleStage] = stage,
        [Model.CrmVersion] = 4L,
        [Model.LegalName] = "Kaffeerösterei Lindner GmbH",
        [Model.ContactEmail] = "kasse@lindner.example",
        [Model.LegalForm] = 771000100,
        [Model.Country] = "DE",
        [Model.Iban] = "DE89370400440532013000",
        [Model.Mcc] = "5812",
        [Model.PricingPlan] = 771000201,
    };

    private static readonly Guid Sales = Guid.NewGuid();
    private static readonly Guid Bridge = Guid.NewGuid();
    private static PluginContext As(Guid user) => new(user, Bridge, Guid.NewGuid);

    [Fact]
    public void A_change_of_a_synced_column_writes_one_outbox_row_and_raises_the_version()
    {
        var sink = new Sink();
        var target = new Rec { [Model.Iban] = "AT611904300234573201", [Model.PricingPlan] = 771000202 };
        var entry = MerchantOutboxLogic.Execute(target, Account(Model.Stage.Live), As(Sales), sink);

        Assert.NotNull(entry);
        Assert.Single(sink.Entries);
        Assert.Equal(5L, target[Model.CrmVersion]);
        Assert.Equal(5L, entry!.Sequence);
        Assert.Equal("crm.merchant.details_changed", entry.EventType);
        Assert.Equal("{\"merchantId\":\"M-000001\",\"changes\":{\"iban\":\"AT611904300234573201\",\"pricingPlan\":\"enterprise\"}}", entry.PayloadJson);
    }

    [Fact]
    public void A_version_sent_by_the_caller_is_ignored()
    {
        var sink = new Sink();
        var target = new Rec { [Model.PricingPlan] = 771000202, [Model.CrmVersion] = 1000L };
        var entry = MerchantOutboxLogic.Execute(target, Account(Model.Stage.Live), As(Sales), sink);
        Assert.Equal(5L, entry!.Sequence);
        Assert.Equal(5L, target[Model.CrmVersion]);
    }

    [Fact]
    public void Writes_by_the_bridge_are_not_echoed_back()
    {
        var sink = new Sink();
        var target = new Rec { [Model.Iban] = "AT611904300234573201" };
        Assert.Null(MerchantOutboxLogic.Execute(target, Account(Model.Stage.Live), As(Bridge), sink));
        Assert.Empty(sink.Entries);
        Assert.False(target.Contains(Model.CrmVersion));
    }

    [Theory]
    [InlineData(771000000)] // prospect: the platform does not know the merchant yet
    [InlineData(771000004)] // rejected
    [InlineData(771000008)] // terminated
    public void Merchants_the_platform_does_not_track_produce_no_events(int stage)
    {
        var sink = new Sink();
        Assert.Null(MerchantOutboxLogic.Execute(new Rec { [Model.Iban] = "AT611904300234573201" }, Account(stage), As(Sales), sink));
        Assert.Empty(sink.Entries);
    }

    [Fact]
    public void Setting_a_column_to_its_current_value_is_not_a_change()
    {
        var sink = new Sink();
        Assert.Null(MerchantOutboxLogic.Execute(new Rec { [Model.Iban] = "DE89370400440532013000" }, Account(Model.Stage.Live), As(Sales), sink));
    }

    [Fact]
    public void Onboarding_needs_every_detail_and_moves_the_stage()
    {
        var sink = new Sink();
        var update = new Rec();
        var entry = RequestOnboardingLogic.Execute(Account(Model.Stage.Prospect), update, As(Sales), sink);
        Assert.Equal(Model.Stage.Onboarding, update[Model.LifecycleStage]);
        Assert.Equal(5L, update[Model.CrmVersion]);
        Assert.Contains("\"legalForm\":\"gmbh\"", entry.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"iban\":\"DE89370400440532013000\"", entry.PayloadJson, StringComparison.Ordinal);

        var incomplete = Account(Model.Stage.Prospect);
        incomplete[Model.Iban] = "DE00";
        incomplete[Model.PricingPlan] = null;
        var ex = Assert.Throws<PluginRuleException>(() => RequestOnboardingLogic.Execute(incomplete, new Rec(), As(Sales), sink));
        Assert.Contains("Pricing plan is missing.", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IBAN", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Onboarding_again_after_a_rejection_clears_the_old_kyc_decision()
    {
        var update = new Rec();
        RequestOnboardingLogic.Execute(Account(Model.Stage.Rejected), update, As(Sales), new Sink());
        Assert.True(update.Contains(Model.KycDecision));
        Assert.Null(update[Model.KycDecision]);
        Assert.Throws<PluginRuleException>(() => RequestOnboardingLogic.Execute(Account(Model.Stage.Live), new Rec(), As(Sales), new Sink()));
    }

    [Fact]
    public void The_merchant_id_is_fixed_once_the_platform_knows_it()
    {
        var ex = Assert.Throws<PluginRuleException>(() =>
            ValidateMerchantLogic.Execute(new Rec { [Model.MerchantId] = "M-000999" }, Account(Model.Stage.Onboarding)));
        Assert.Contains("cannot change", ex.Message, StringComparison.Ordinal);
        ValidateMerchantLogic.Execute(new Rec { [Model.MerchantId] = "M-000999" }, Account(Model.Stage.Prospect));
    }

    [Fact]
    public void Json_escapes_quotes_and_control_characters()
    {
        var json = MerchantCrmBridge.Plugins.Logic.Json.Flat([new("legalName", "Café \"Zum Hafen\"\n")]);
        Assert.Equal("{\"legalName\":\"Café \\\"Zum Hafen\\\"\\n\"}", json);
        Assert.NotNull(System.Text.Json.Nodes.JsonNode.Parse(json));
    }

    internal sealed class Rec : Dictionary<string, object?>, IRecord
    {
        public Rec() : base(StringComparer.Ordinal) { }
        public bool Contains(string column) => ContainsKey(column);
        public object? Get(string column) => TryGetValue(column, out var v) ? v : null;
        public void Set(string column, object? value) => this[column] = value;
    }

    internal sealed class Sink : IOutboxSink
    {
        public List<OutboxEntry> Entries { get; } = [];
        public void Write(OutboxEntry entry) => Entries.Add(entry);
    }
}
