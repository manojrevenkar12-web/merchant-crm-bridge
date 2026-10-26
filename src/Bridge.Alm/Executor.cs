using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Bridge.Alm;

public sealed record StepResult(Step Step, string Outcome, string? Id);

/// <summary>Runs a plan against an environment. Every step looks before it writes, so running the plan again
/// changes nothing that is already there; creates carry the solution header, so they land in the solution.</summary>
public sealed partial class Executor(HttpClient http, Uri environment, string solutionUniqueName, IReadOnlyDictionary<string, string> files)
{
    private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);
    private readonly Uri _api = new(environment, "api/data/v9.2/");

    [GeneratedRegex(@"\{\{id:([^}]+)\}\}")]
    private static partial Regex IdRef();

    [GeneratedRegex(@"\{\{file:([^}]+)\}\}")]
    private static partial Regex FileRef();

    [GeneratedRegex(@"\(([0-9a-fA-F-]{36})\)$")]
    private static partial Regex EntityIdInUrl();

    public async Task<IReadOnlyList<StepResult>> ApplyAsync(IReadOnlyList<Step> plan, Action<StepResult>? progress, CancellationToken ct)
    {
        var results = new List<StepResult>();
        foreach (var step in plan)
        {
            var result = await RunAsync(step, ct).ConfigureAwait(false);
            if (result.Id is not null) _ids[step.Id] = result.Id;
            results.Add(result);
            progress?.Invoke(result);
        }
        return results;
    }

    private async Task<StepResult> RunAsync(Step step, CancellationToken ct)
    {
        string? found = null;
        if (step.FindPath is not null) found = await FindAsync(Resolve(step.FindPath), step.IdField!, ct).ConfigureAwait(false);

        switch (step.Kind)
        {
            case StepKind.Lookup:
                return found is null ? throw new InvalidOperationException($"{step.Description}: not found in the environment") : new(step, "found", found);
            case StepKind.Ensure when found is not null:
                return new(step, "exists", found);
            case StepKind.Upsert when found is not null:
                _ids[step.Id] = found;
                await SendAsync(HttpMethod.Patch, Resolve(step.UpdatePath!), step.Body, ct).ConfigureAwait(false);
                return new(step, "updated", found);
            case StepKind.Action:
                await SendAsync(HttpMethod.Post, Resolve(step.Path!), step.Body, ct).ConfigureAwait(false);
                return new(step, "done", null);
            default:
                var location = await SendAsync(HttpMethod.Post, Resolve(step.Path!), step.Body, ct).ConfigureAwait(false);
                var id = location is not null && EntityIdInUrl().Match(location) is { Success: true } match ? match.Groups[1].Value
                    : step.FindPath is not null ? await FindAsync(Resolve(step.FindPath), step.IdField!, ct).ConfigureAwait(false) : null;
                return new(step, "created", id);
        }
    }

    private string Resolve(string text) =>
        FileRef().Replace(IdRef().Replace(text, m => _ids.TryGetValue(m.Groups[1].Value, out var id) ? id
            : throw new InvalidOperationException($"no id captured for {m.Groups[1].Value}")),
            m => files.TryGetValue(m.Groups[1].Value, out var path) ? Convert.ToBase64String(File.ReadAllBytes(path))
                : throw new InvalidOperationException($"file '{m.Groups[1].Value}' was not given (--{m.Groups[1].Value} <path>)"));

    private JsonNode? Resolve(JsonNode? body) => body is null ? null : JsonNode.Parse(Resolve(body.ToJsonString()));

    private async Task<string?> FindAsync(string path, string idField, CancellationToken ct)
    {
        using var response = await http.GetAsync(new Uri(_api, path), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, "GET " + path, ct).ConfigureAwait(false);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))!;
        var row = json["value"] is JsonArray values ? values.FirstOrDefault() : json;
        return row?[idField]?.GetValue<string>();
    }

    private async Task<string?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(_api, path))
        {
            Content = new StringContent(Resolve(body)?.ToJsonString() ?? "{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("MSCRM.SolutionUniqueName", solutionUniqueName);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"{method} {path}", ct).ConfigureAwait(false);
        return response.Headers.TryGetValues("OData-EntityId", out var ids) ? ids.FirstOrDefault() : null;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new HttpRequestException($"{what}: {(int)response.StatusCode} {text[..Math.Min(text.Length, 500)]}", null, response.StatusCode);
    }

    public static HttpClient Client(string token)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
        http.DefaultRequestHeaders.Add("OData-Version", "4.0");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }
}
