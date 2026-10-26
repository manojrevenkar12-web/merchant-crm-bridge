using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bridge.Alm;

public sealed record ChoiceOption(int Value, string Name, string Label);
public sealed record Choice(string Name, string DisplayName, IReadOnlyList<ChoiceOption> Options);
public sealed record Column(string Name, string Type, string? DisplayName, int? MaxLength, int? Precision, string? Choice, string Owner, bool Standard);
public sealed record Key(string Name, IReadOnlyList<string> Attributes);
public sealed record Table(string LogicalName, string EntitySet, string PrimaryId, string PrimaryName, string DisplayName, bool Standard, string Ownership, IReadOnlyList<Key> Keys, IReadOnlyList<Column> Columns);
public sealed record PluginStep(string Plugin, string Message, string Entity, string Stage, string Mode, int Rank, IReadOnlyList<string> FilteringAttributes, IReadOnlyList<string> PreImage);
public sealed record CustomApi(string UniqueName, string DisplayName, string BoundEntity, bool IsFunction, string Plugin, IReadOnlyList<(string UniqueName, string Type)> ResponseProperties);
public sealed record EnvironmentVariable(string SchemaName, string DisplayName, string Type, string DefaultValue);
public sealed record ConnectionReference(string LogicalName, string DisplayName, string ConnectorId);
public sealed record SecurityRole(string Name, string Description, IReadOnlyDictionary<string, IReadOnlyList<string>> Privileges);
public sealed record FieldSecurityProfile(string Name, string Description, IReadOnlyList<string> Owners, bool Read, bool Update, bool Create);
public sealed record AppArea(string Title, IReadOnlyList<(string Title, IReadOnlyList<string> Tables)> Groups);
public sealed record App(string UniqueName, string DisplayName, string Description, IReadOnlyList<AppArea> Areas);
public sealed record Flow(string Name, string UniqueName, string Definition);

/// <summary>dataverse/model.json, typed.</summary>
public sealed record DataverseModel(
    string PublisherUniqueName, string PublisherDisplayName, string Prefix, int OptionValuePrefix,
    string SolutionUniqueName, string SolutionDisplayName, string SolutionVersion,
    IReadOnlyList<Choice> Choices, IReadOnlyList<Table> Tables, IReadOnlyList<PluginStep> PluginSteps, IReadOnlyList<CustomApi> CustomApis,
    IReadOnlyList<EnvironmentVariable> EnvironmentVariables, IReadOnlyList<ConnectionReference> ConnectionReferences,
    IReadOnlyList<SecurityRole> SecurityRoles, IReadOnlyList<FieldSecurityProfile> FieldSecurityProfiles, App App, IReadOnlyList<Flow> Flows,
    string Directory)
{
    public static DataverseModel Load(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        static string S(JsonNode? n) => n!.GetValue<string>();
        static IReadOnlyList<string> List(JsonNode? n) => n is JsonArray a ? a.Select(x => S(x)).ToList() : [];

        var choices = root["choices"]!.AsObject().Select(kv => new Choice(kv.Key, S(kv.Value!["displayName"]),
            kv.Value!["options"]!.AsArray().Select(o => new ChoiceOption(o!["value"]!.GetValue<int>(), S(o["name"]), S(o["label"]))).ToList())).ToList();

        var tables = root["tables"]!.AsArray().Select(t => new Table(
            S(t!["logicalName"]), S(t["entitySet"]), S(t["primaryId"]), S(t["primaryName"]), S(t["displayName"]),
            t["standard"]?.GetValue<bool>() ?? false, t["ownership"]?.GetValue<string>() ?? "UserOwned",
            t["keys"]!.AsArray().Select(k => new Key(S(k!["name"]), List(k["attributes"]))).ToList(),
            t["columns"]!.AsArray().Select(c => new Column(S(c!["name"]), S(c["type"]), c["displayName"]?.GetValue<string>(), c["maxLength"]?.GetValue<int>(),
                c["precision"]?.GetValue<int>(), c["choice"]?.GetValue<string>(), c["owner"]?.GetValue<string>() ?? "crm", c["standard"]?.GetValue<bool>() ?? false)).ToList())).ToList();

        var steps = root["pluginSteps"]!.AsArray().Select(s => new PluginStep(S(s!["plugin"]), S(s["message"]), S(s["entity"]), S(s["stage"]), S(s["mode"]),
            s["rank"]!.GetValue<int>(), List(s["filteringAttributes"]), List(s["preImage"]))).ToList();

        var apis = root["customApis"]!.AsArray().Select(a => new CustomApi(S(a!["uniqueName"]), S(a["displayName"]), S(a["boundEntity"]), a["isFunction"]!.GetValue<bool>(), S(a["plugin"]),
            a["responseProperties"]!.AsArray().Select(p => (S(p!["uniqueName"]), S(p["type"]))).ToList())).ToList();

        var envVars = root["environmentVariables"]!.AsArray().Select(e => new EnvironmentVariable(S(e!["schemaName"]), S(e["displayName"]), S(e["type"]), S(e["defaultValue"]))).ToList();
        var connRefs = root["connectionReferences"]!.AsArray().Select(c => new ConnectionReference(S(c!["logicalName"]), S(c["displayName"]), S(c["connectorId"]))).ToList();
        var roles = root["securityRoles"]!.AsArray().Select(r => new SecurityRole(S(r!["name"]), S(r["description"]),
            r["privileges"]!.AsObject().ToDictionary(kv => kv.Key, kv => List(kv.Value)))).ToList();
        var profiles = root["fieldSecurityProfiles"]!.AsArray().Select(f => new FieldSecurityProfile(S(f!["name"]), S(f["description"]), List(f["owners"]),
            f["read"]!.GetValue<bool>(), f["update"]!.GetValue<bool>(), f["create"]!.GetValue<bool>())).ToList();
        var app = root["app"]!;
        var areas = app["areas"]!.AsArray().Select(a => new AppArea(S(a!["title"]),
            a["groups"]!.AsArray().Select(g => (S(g!["title"]), List(g["tables"]))).ToList())).ToList();
        var flows = root["flows"]!.AsArray().Select(f => new Flow(S(f!["name"]), S(f["uniqueName"]), S(f["definition"]))).ToList();

        var publisher = root["publisher"]!;
        var solution = root["solution"]!;
        return new DataverseModel(S(publisher["uniqueName"]), S(publisher["displayName"]), S(publisher["prefix"]), publisher["optionValuePrefix"]!.GetValue<int>(),
            S(solution["uniqueName"]), S(solution["displayName"]), S(solution["version"]),
            choices, tables, steps, apis, envVars, connRefs, roles, profiles,
            new App(S(app["uniqueName"]), S(app["displayName"]), S(app["description"]), areas), flows, Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    /// <summary>Schema names carry the publisher prefix and a capital after it: mcb_terminalref → mcb_Terminalref.</summary>
    public string SchemaName(string logicalName) =>
        logicalName.StartsWith(Prefix + "_", StringComparison.Ordinal) && logicalName.Length > Prefix.Length + 1
            ? string.Concat(Prefix, "_", char.ToUpperInvariant(logicalName[Prefix.Length + 1]).ToString(), logicalName.AsSpan(Prefix.Length + 2))
            : logicalName;

    public string ReadText(string relative) => File.ReadAllText(System.IO.Path.Combine(Directory, relative));

    internal static JsonSerializerOptions Json { get; } = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
