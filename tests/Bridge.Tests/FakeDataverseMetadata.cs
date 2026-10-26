using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Bridge.Tests;

/// <summary>
/// Just enough of the Dataverse Web API for the metadata plan: records with $filter on equality, lookups
/// through @odata.bind, EntityDefinitions with Attributes and Keys, global choices, actions. Lookups the
/// plan finds work only if they match what its creates wrote, which is the point.
/// </summary>
public sealed partial class FakeDataverseMetadata : HttpMessageHandler
{
    private readonly Dictionary<string, List<JsonObject>> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, JsonObject> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, JsonObject> _choices = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Writes { get; } = [];
    public List<(string Path, string Header)> SolutionHeaders { get; } = [];

    public FakeDataverseMetadata(IEnumerable<string> privileges)
    {
        Seed("businessunits", new JsonObject { ["name"] = "root" });
        var create = Seed("sdkmessages", new JsonObject { ["name"] = "Create" });
        var update = Seed("sdkmessages", new JsonObject { ["name"] = "Update" });
        Seed("sdkmessagefilters", new JsonObject { ["primaryobjecttypecode"] = "account", ["sdkmessageid@odata.bind"] = $"/sdkmessages({create})" });
        Seed("sdkmessagefilters", new JsonObject { ["primaryobjecttypecode"] = "account", ["sdkmessageid@odata.bind"] = $"/sdkmessages({update})" });
        foreach (var p in privileges) Seed("privileges", new JsonObject { ["name"] = p });
        _tables["account"] = new JsonObject { ["MetadataId"] = Guid.NewGuid().ToString(), ["Attributes"] = new JsonArray(), ["Keys"] = new JsonArray() };
    }

    private string Seed(string set, JsonObject row)
    {
        var id = Guid.NewGuid().ToString();
        row["__id"] = id;
        if (!_records.TryGetValue(set, out var list)) _records[set] = list = [];
        list.Add(row);
        return id;
    }

    [GeneratedRegex(@"^EntityDefinitions\(LogicalName='([^']+)'\)(?:/(Attributes|Keys)(?:\(LogicalName='([^']+)'\))?)?$")]
    private static partial Regex EntityPath();

    [GeneratedRegex(@"^GlobalOptionSetDefinitions(?:\(Name='([^']+)'\))?$")]
    private static partial Regex ChoicePath();

    [GeneratedRegex(@"^([a-z]+)\(([0-9a-f-]{36})\)$")]
    private static partial Regex RecordPath();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var full = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
        var path = full[(full.IndexOf("/api/data/v9.2/", StringComparison.Ordinal) + "/api/data/v9.2/".Length)..];
        var query = path.Contains('?', StringComparison.Ordinal) ? path[(path.IndexOf('?', StringComparison.Ordinal) + 1)..] : "";
        if (query.Length > 0) path = path[..path.IndexOf('?', StringComparison.Ordinal)];
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
        if (request.Method != HttpMethod.Get)
        {
            Writes.Add($"{request.Method} {path}");
            if (request.Headers.TryGetValues("MSCRM.SolutionUniqueName", out var h)) SolutionHeaders.Add((path, h.First()));
            Assert.DoesNotContain("{{", body?.ToJsonString() ?? "", StringComparison.Ordinal);
        }

        if (EntityPath().Match(path) is { Success: true } e)
        {
            if (!_tables.TryGetValue(e.Groups[1].Value, out var table)) return Status(HttpStatusCode.NotFound);
            if (!e.Groups[2].Success) return Json(table);
            var list = table[e.Groups[2].Value]!.AsArray();
            if (request.Method == HttpMethod.Post)
            {
                var item = body!.DeepClone().AsObject();
                item["LogicalName"] = item["SchemaName"]!.GetValue<string>().ToLowerInvariant();
                item["MetadataId"] = Guid.NewGuid().ToString();
                if (e.Groups[2].Value == "Attributes" && item["GlobalOptionSet@odata.bind"] is { } bind)
                    Assert.Contains(_choices.Values, c => bind.GetValue<string>().Contains(c["MetadataId"]!.GetValue<string>(), StringComparison.Ordinal));
                list.Add(item);
                return Created($"EntityDefinitions({table["MetadataId"]})/{e.Groups[2].Value}({item["MetadataId"]})");
            }
            var found = list.FirstOrDefault(x => x!["LogicalName"]!.GetValue<string>() == e.Groups[3].Value);
            return found is null ? Status(HttpStatusCode.NotFound) : Json(found.AsObject());
        }
        if (path == "EntityDefinitions" && request.Method == HttpMethod.Post)
        {
            var name = body!["SchemaName"]!.GetValue<string>().ToLowerInvariant();
            var id = Guid.NewGuid().ToString();
            var attributes = new JsonArray(body["Attributes"]!.AsArray().Select(a =>
            {
                var c = a!.DeepClone().AsObject();
                c["LogicalName"] = c["SchemaName"]!.GetValue<string>().ToLowerInvariant();
                return (JsonNode)c;
            }).ToArray());
            _tables[name] = new JsonObject { ["MetadataId"] = id, ["Attributes"] = attributes, ["Keys"] = new JsonArray(), ["OwnershipType"] = body["OwnershipType"]!.DeepClone() };
            return Created($"EntityDefinitions({id})");
        }
        if (ChoicePath().Match(path) is { Success: true } c)
        {
            if (request.Method == HttpMethod.Post)
            {
                var id = Guid.NewGuid().ToString();
                _choices[body!["Name"]!.GetValue<string>()] = new JsonObject { ["MetadataId"] = id, ["Options"] = body["Options"]!.DeepClone() };
                return Created($"GlobalOptionSetDefinitions({id})");
            }
            return _choices.TryGetValue(c.Groups[1].Value, out var choice) ? Json(choice) : Status(HttpStatusCode.NotFound);
        }
        if (path.Contains("Microsoft.Dynamics.CRM.", StringComparison.Ordinal) || path is "AddAppComponents" or "PublishAllXml")
            return Status(HttpStatusCode.NoContent);
        if (RecordPath().Match(path) is { Success: true } r && request.Method == HttpMethod.Patch)
        {
            var row = _records[r.Groups[1].Value].Single(x => x["__id"]!.GetValue<string>() == r.Groups[2].Value);
            foreach (var (k, v) in body!) row[k] = v?.DeepClone();
            return Status(HttpStatusCode.NoContent);
        }
        if (request.Method == HttpMethod.Post)
            return Created($"{path}({Seed(path, body!.DeepClone().AsObject())})");

        // GET collection?$select=x&$filter=a eq 'b' and _c_value eq guid
        var select = Regex.Match(query, @"\$select=([^&]+)").Groups[1].Value;
        var filter = Regex.Match(query, @"\$filter=(.+)$").Groups[1].Value;
        var rows = (_records.GetValueOrDefault(path) ?? []).Where(row => Matches(row, filter)).ToList();
        var value = new JsonArray(rows.Select(row => (JsonNode)new JsonObject { [select] = row["__id"]!.GetValue<string>() }).ToArray());
        return Json(new JsonObject { ["value"] = value });
    }

    private static bool Matches(JsonObject row, string filter)
    {
        if (filter.Length == 0) return true;
        foreach (var clause in filter.Split(" and "))
        {
            var parts = clause.Split(" eq ", 2);
            var (field, expected) = (parts[0].Trim(), parts[1].Trim().Trim('\''));
            string? actual;
            if (field.StartsWith('_') && field.EndsWith("_value", StringComparison.Ordinal))
            {
                var nav = field[1..^"_value".Length];
                var bind = row.FirstOrDefault(kv => kv.Key.Equals(nav + "@odata.bind", StringComparison.OrdinalIgnoreCase)).Value?.GetValue<string>();
                actual = bind is null ? null : Regex.Match(bind, @"\(([0-9a-f-]{36})\)").Groups[1].Value;
            }
            else
            {
                actual = row[field]?.ToString();
            }
            if (expected == "null" ? actual is not null : !string.Equals(actual, expected, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    public int Count(string set) => _records.GetValueOrDefault(set)?.Count ?? 0;
    public JsonObject? Table(string name) => _tables.GetValueOrDefault(name);
    public IReadOnlyList<JsonObject> Records(string set) => _records.GetValueOrDefault(set) ?? [];

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    private static HttpResponseMessage Json(JsonObject o) => new(HttpStatusCode.OK) { Content = new StringContent(o.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Created(string relative)
    {
        var response = new HttpResponseMessage(HttpStatusCode.NoContent);
        response.Headers.Add("OData-EntityId", "https://contoso.crm4.dynamics.com/api/data/v9.2/" + relative);
        return response;
    }
}
