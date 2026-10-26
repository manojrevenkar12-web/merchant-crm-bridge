using System.Text.Json.Nodes;

namespace Bridge.Alm;

/// <summary>
/// Deployment settings for a managed import (the file `pac solution import --settings-file` and the Power
/// Platform import actions take): a value for every environment variable and a connection for every
/// connection reference. Values in alm/settings/&lt;environment&gt;.json are placeholders such as
/// #{INTEGRATION_USER_ID}# that the pipeline fills from the GitHub environment's variables.
/// </summary>
public static class DeploymentSettings
{
    public static JsonObject Template(DataverseModel m) => new()
    {
        ["EnvironmentVariables"] = new JsonArray(m.EnvironmentVariables.Select(e => (JsonNode)new JsonObject { ["SchemaName"] = e.SchemaName, ["Value"] = "" }).ToArray()),
        ["ConnectionReferences"] = new JsonArray(m.ConnectionReferences.Select(c => (JsonNode)new JsonObject
        {
            ["LogicalName"] = c.LogicalName,
            ["ConnectionId"] = "",
            ["ConnectorId"] = c.ConnectorId,
        }).ToArray()),
    };

    /// <summary>What is wrong with a settings file: missing or unknown names, wrong connectors, empty values.</summary>
    public static IReadOnlyList<string> Check(DataverseModel m, JsonNode settings, string name)
    {
        var problems = new List<string>();
        var vars = settings["EnvironmentVariables"]?.AsArray().ToDictionary(v => v!["SchemaName"]!.GetValue<string>(), v => v!["Value"]?.GetValue<string>()) ?? [];
        foreach (var e in m.EnvironmentVariables)
        {
            if (!vars.TryGetValue(e.SchemaName, out var value)) problems.Add($"{name}: no value for environment variable {e.SchemaName}");
            else if (string.IsNullOrWhiteSpace(value)) problems.Add($"{name}: environment variable {e.SchemaName} is empty");
            else if (e.Type == "Boolean" && value is not ("true" or "false") && !value.StartsWith("#{", StringComparison.Ordinal))
                problems.Add($"{name}: {e.SchemaName} must be true or false");
        }
        problems.AddRange(vars.Keys.Except(m.EnvironmentVariables.Select(e => e.SchemaName)).Select(k => $"{name}: {k} is not an environment variable of the solution"));

        var refs = settings["ConnectionReferences"]?.AsArray().ToDictionary(c => c!["LogicalName"]!.GetValue<string>(), c => c!) ?? [];
        foreach (var c in m.ConnectionReferences)
        {
            if (!refs.TryGetValue(c.LogicalName, out var r)) { problems.Add($"{name}: no connection for {c.LogicalName}"); continue; }
            if (r["ConnectorId"]?.GetValue<string>() != c.ConnectorId) problems.Add($"{name}: {c.LogicalName} must use connector {c.ConnectorId}");
            if (string.IsNullOrWhiteSpace(r["ConnectionId"]?.GetValue<string>())) problems.Add($"{name}: {c.LogicalName} has no connection id");
        }
        problems.AddRange(refs.Keys.Except(m.ConnectionReferences.Select(c => c.LogicalName)).Select(k => $"{name}: {k} is not a connection reference of the solution"));
        return problems;
    }

    /// <summary>Replaces #{NAME}# with the environment variable NAME; fails when one is not set.</summary>
    public static string Fill(string text, Func<string, string?> lookup) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"#\{([A-Z0-9_]+)\}#", m =>
            lookup(m.Groups[1].Value) is { Length: > 0 } v ? System.Text.Json.JsonEncodedText.Encode(v).ToString() : throw new InvalidOperationException($"{m.Groups[1].Value} is not set"));
}
