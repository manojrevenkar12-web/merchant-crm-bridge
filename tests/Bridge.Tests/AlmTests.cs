using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bridge.Alm;

namespace Bridge.Tests;

public class AlmTests
{
    private static readonly DataverseModel Model = DataverseModel.Load(Path.Combine(ModelConsistencyTests.RepoRoot(), "dataverse", "model.json"));
    private static readonly IReadOnlyList<Step> PlanSteps = Plan.Build(Model);
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static IEnumerable<string> Privileges() =>
        Model.SecurityRoles.SelectMany(r => r.Privileges.SelectMany(p => p.Value.Select(v => $"prv{v}{(p.Key == "account" ? "Account" : p.Key)}"))).Distinct();

    [Fact]
    public void Every_column_is_created_once_with_the_right_type()
    {
        var types = new Dictionary<string, string>
        {
            ["string"] = "StringAttributeMetadata", ["memo"] = "MemoAttributeMetadata", ["int"] = "IntegerAttributeMetadata", ["bigint"] = "BigIntAttributeMetadata",
            ["decimal"] = "DecimalAttributeMetadata", ["datetime"] = "DateTimeAttributeMetadata", ["choice"] = "PicklistAttributeMetadata",
        };
        foreach (var table in Model.Tables)
        {
            foreach (var column in table.Columns.Where(c => !c.Standard))
            {
                JsonObject attribute;
                if (!table.Standard && column.Name == table.PrimaryName)
                {
                    var create = PlanSteps.Single(s => s.Id == $"table:{table.LogicalName}");
                    attribute = create.Body!["Attributes"]![0]!.AsObject();
                    Assert.True(attribute["IsPrimaryName"]!.GetValue<bool>());
                }
                else
                {
                    attribute = PlanSteps.Single(s => s.Id == $"column:{table.LogicalName}.{column.Name}").Body!;
                }
                Assert.Equal("Microsoft.Dynamics.CRM." + types[column.Type], attribute["@odata.type"]!.GetValue<string>());
                Assert.Equal(column.Name, attribute["SchemaName"]!.GetValue<string>().ToLowerInvariant());
                Assert.Equal(column.Owner == "platform" && table.Standard, attribute["IsSecured"]?.GetValue<bool>() ?? false);
                if (column.Type == "choice") Assert.Equal($"/GlobalOptionSetDefinitions({{{{id:choice:{column.Choice}}}}})", attribute["GlobalOptionSet@odata.bind"]!.GetValue<string>());
                if (column.Type == "decimal") Assert.Equal(column.Precision, attribute["Precision"]!.GetValue<int>());
            }
        }
        Assert.DoesNotContain(PlanSteps, s => s.Id is "column:account.name" or "column:account.emailaddress1"); // standard columns are left alone
    }

    [Fact]
    public void Choice_values_carry_the_publisher_option_prefix()
    {
        foreach (var step in PlanSteps.Where(s => s.Id.StartsWith("choice:", StringComparison.Ordinal)))
            Assert.All(step.Body!["Options"]!.AsArray(), o => Assert.Equal(Model.OptionValuePrefix, o!["Value"]!.GetValue<int>() / 10000));
    }

    [Fact]
    public void Plug_in_steps_are_registered_as_the_model_says()
    {
        var outbox = PlanSteps.Single(s => s.Id == "step:MerchantCrmBridge.Plugins.MerchantOutboxPlugin: Update of account");
        Assert.Equal((20, 0, 10), (outbox.Body!["stage"]!.GetValue<int>(), outbox.Body["mode"]!.GetValue<int>(), outbox.Body["rank"]!.GetValue<int>()));
        Assert.Equal("name,emailaddress1,mcb_legalform,mcb_country,mcb_iban,mcb_mcc,mcb_pricingplan", outbox.Body["filteringattributes"]!.GetValue<string>());
        var image = PlanSteps.Single(s => s.Id == "image:MerchantCrmBridge.Plugins.MerchantOutboxPlugin: Update of account");
        Assert.Contains("mcb_crmversion", image.Body!["attributes"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("Target", image.Body["messagepropertyname"]!.GetValue<string>());
        var validate = PlanSteps.Single(s => s.Id == "step:MerchantCrmBridge.Plugins.ValidateMerchantPlugin: Create of account");
        Assert.Equal(10, validate.Body!["stage"]!.GetValue<int>());
        var api = PlanSteps.Single(s => s.Id == "api:mcb_RequestOnboarding");
        Assert.Equal(("account", 1, false), (api.Body!["boundentitylogicalname"]!.GetValue<string>(), api.Body["bindingtype"]!.GetValue<int>(), api.Body["isfunction"]!.GetValue<bool>()));
    }

    [Fact]
    public void Every_reference_points_to_an_earlier_step()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in PlanSteps)
        {
            var text = string.Join(" ", step.FindPath, step.Path, step.UpdatePath, step.Body?.ToJsonString());
            foreach (Match m in Regex.Matches(text, @"\{\{id:([^}]+)\}\}"))
                Assert.True(seen.Contains(m.Groups[1].Value) || m.Groups[1].Value == step.Id && step.Kind == StepKind.Upsert, $"{step.Id} uses {m.Groups[1].Value} before it exists");
            seen.Add(step.Id);
        }
    }

    [Fact]
    public void The_committed_plan_is_up_to_date()
    {
        var committed = File.ReadAllText(Path.Combine(ModelConsistencyTests.RepoRoot(), "alm", "metadata-plan.json"));
        Assert.True(committed == PlanDocument.Render(Model, PlanSteps), "alm/metadata-plan.json is stale: dotnet run --project src/Bridge.Alm -- plan --out alm/metadata-plan.json");
    }

    [Fact]
    public async Task Applying_twice_creates_everything_once_and_then_nothing()
    {
        var dataverse = new FakeDataverseMetadata(Privileges());
        var plugins = Path.Combine(Path.GetTempPath(), $"plugins-{Guid.NewGuid():N}.dll");
        await File.WriteAllBytesAsync(plugins, [0x4D, 0x5A, 1, 2, 3], _ct);
        using var http = new HttpClient(dataverse);
        var files = new Dictionary<string, string> { ["plugins"] = plugins };

        var first = await new Executor(http, new Uri("https://contoso.crm4.dynamics.com/"), Model.SolutionUniqueName, files).ApplyAsync(PlanSteps, null, _ct);
        Assert.Equal(PlanSteps.Count(s => s.Kind is StepKind.Ensure or StepKind.Upsert), first.Count(r => r.Outcome == "created"));
        Assert.All(dataverse.SolutionHeaders, h => Assert.Equal("MerchantCrmBridge", h.Header));
        Assert.Equal(Model.Tables.Count(t => !t.Standard), Model.Tables.Count(t => dataverse.Table(t.LogicalName) is { } x && x["OwnershipType"] is not null));
        Assert.Equal("TVoBAgM=", dataverse.Records("pluginassemblies").Single()["content"]!.GetValue<string>());
        Assert.Equal(Model.FieldSecurityProfiles.Sum(p => Model.Tables.Sum(t => t.Columns.Count(c => Plan.Secured(t, c) && p.Owners.Contains(c.Owner)))), dataverse.Count("fieldpermissions"));

        var writes = dataverse.Writes.Count;
        var second = await new Executor(http, new Uri("https://contoso.crm4.dynamics.com/"), Model.SolutionUniqueName, files).ApplyAsync(PlanSteps, null, _ct);
        Assert.DoesNotContain(second, r => r.Outcome == "created");
        Assert.Equal(PlanSteps.Count(s => s.Kind == StepKind.Upsert), second.Count(r => r.Outcome == "updated"));
        Assert.Equal(1, dataverse.Count("pluginassemblies"));
        Assert.Equal(PlanSteps.Count(s => s.Kind is StepKind.Upsert or StepKind.Action), dataverse.Writes.Count - writes);
        File.Delete(plugins);
    }

    [Fact]
    public async Task A_missing_platform_record_stops_the_run_with_its_name()
    {
        var dataverse = new FakeDataverseMetadata(Privileges().Where(p => p != "prvCreatemcb_terminal"));
        using var http = new HttpClient(dataverse);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Executor(http, new Uri("https://contoso.crm4.dynamics.com/"), Model.SolutionUniqueName, new Dictionary<string, string>()).ApplyAsync(PlanSteps, null, _ct));
        Assert.Contains("prvCreatemcb_terminal", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_environment_has_complete_deployment_settings()
    {
        var dir = Path.Combine(ModelConsistencyTests.RepoRoot(), "alm", "settings");
        var files = Directory.GetFiles(dir, "*.json");
        Assert.Contains(files, f => f.EndsWith("test.json", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("prod.json", StringComparison.Ordinal));
        foreach (var f in files) Assert.Empty(DeploymentSettings.Check(Model, JsonNode.Parse(File.ReadAllText(f))!, Path.GetFileName(f)));
    }

    [Fact]
    public void Settings_checks_catch_gaps_and_placeholders_are_filled_safely()
    {
        var template = DeploymentSettings.Template(Model);
        var problems = DeploymentSettings.Check(Model, template, "empty.json");
        Assert.Equal(Model.EnvironmentVariables.Count + Model.ConnectionReferences.Count, problems.Count);
        var filled = DeploymentSettings.Fill("""{"Value":"#{USER}#"}""", n => n == "USER" ? "a\"b" : null);
        Assert.Equal("a\"b", JsonNode.Parse(filled)!["Value"]!.GetValue<string>());
        Assert.Throws<InvalidOperationException>(() => DeploymentSettings.Fill("#{MISSING}#", _ => null));
    }
}
