using System.Text.Json;
using System.Text.Json.Nodes;
using Bridge.Alm;

// mcb-alm plan    [--model dataverse/model.json] [--out alm/metadata-plan.json]
// mcb-alm apply   --url https://org.crm4.dynamics.com/ --plugins path/to/MerchantCrmBridge.Plugins.dll   (token in DATAVERSE_TOKEN)
// mcb-alm settings --check alm/settings              (every environment's file is complete)
// mcb-alm settings --fill alm/settings/test.json --out deploy/test.json   (placeholders from the environment)
var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 1; i < args.Length; i++)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal)) { Console.Error.WriteLine($"unexpected {args[i]}"); return 2; }
    options[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
}
var model = DataverseModel.Load(options.GetValueOrDefault("model") ?? Path.Combine(AppContext.BaseDirectory, "dataverse", "model.json"));

switch (args.FirstOrDefault())
{
    case "plan":
    {
        var plan = Plan.Build(model);
        var json = PlanDocument.Render(model, plan);
        if (options.TryGetValue("out", out var outPath)) await File.WriteAllTextAsync(outPath, json);
        else foreach (var s in plan) Console.WriteLine($"{s.Kind,-7} {s.Description}");
        Console.Error.WriteLine($"{plan.Count} steps");
        return 0;
    }
    case "apply":
    {
        var url = new Uri(options["url"].TrimEnd('/') + "/");
        var token = Environment.GetEnvironmentVariable("DATAVERSE_TOKEN") ?? throw new InvalidOperationException("DATAVERSE_TOKEN is not set");
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (options.TryGetValue("plugins", out var plugins)) files["plugins"] = plugins;
        using var http = Executor.Client(token);
        var executor = new Executor(http, url, model.SolutionUniqueName, files);
        var results = await executor.ApplyAsync(Plan.Build(model), r => Console.WriteLine($"{r.Outcome,-8} {r.Step.Description}"), CancellationToken.None);
        Console.WriteLine($"{results.Count(r => r.Outcome == "created")} created, {results.Count(r => r.Outcome == "updated")} updated, {results.Count(r => r.Outcome == "exists")} unchanged");
        return 0;
    }
    case "settings" when options.TryGetValue("check", out var dir):
    {
        var problems = Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal)
            .SelectMany(f => DeploymentSettings.Check(model, JsonNode.Parse(File.ReadAllText(f))!, Path.GetFileName(f))).ToList();
        foreach (var p in problems) Console.Error.WriteLine(p);
        return problems.Count == 0 ? 0 : 1;
    }
    case "settings" when options.TryGetValue("fill", out var file):
    {
        var filled = DeploymentSettings.Fill(File.ReadAllText(file), Environment.GetEnvironmentVariable);
        await File.WriteAllTextAsync(options["out"], filled);
        return 0;
    }
    case "settings":
        Console.WriteLine(DeploymentSettings.Template(model).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    default:
        Console.Error.WriteLine("usage: mcb-alm plan|apply|settings [options]");
        return 2;
}
