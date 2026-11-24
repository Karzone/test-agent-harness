using FixHarness.Harness;
using FixHarness.Testing;

namespace FixHarness.Cli;

public static class Commands
{
    public const string Help = """
        fixharness list [--repo <path>]
            List the planted failures in failures/manifest.json.

        fixharness run [--repo <path>] --failure <id>
        fixharness run [--repo <path>] --filter <dotnet test filter>
            Try to fix one failing test. Options:
              --model <name>      model name, or scripted:<script.json> to replay a script
              --attempts <n>      attempts before giving up (default 3)
              --verify-runs <n>   times the fixed test must pass (default 3)
              --out <dir>         where to write fix.patch, report.md, trace.json

        Model: set FIXHARNESS_API_KEY, FIXHARNESS_MODEL and optionally FIXHARNESS_BASE_URL
        (any OpenAI-compatible endpoint: OpenAI, Azure OpenAI, OpenRouter).

        Exit codes: 0 fixed, 1 not fixed, 2 nothing to do or bad usage.
        """;

    public static int List(string repo)
    {
        foreach (var item in PlantedFailure.LoadAll(repo))
        {
            Console.WriteLine($"{item.Id,-22} {item.Kind,-15} {item.Title}");
        }
        return 0;
    }

    public static async Task<int> RunAsync(CliOptions options, string repo)
    {
        var config = RepoConfig.Load(repo);
        var failure = FindFailure(options, repo);
        var filter = options.Get("filter") ?? failure?.Filter ?? throw new ArgumentException("Pass --failure or --filter.");

        var model = ModelFactory.Create(options.Get("model"), repo);
        var harness = new FixHarnessRunner(model, new DotnetTestRunner(config.TestProject), Console.WriteLine);

        Console.WriteLine($"Model: {model.Name}\nTest:  {filter}\n");
        var result = await harness.RunAsync(new FixRequest(
            repo,
            filter,
            config.TestRoots,
            failure?.Patch,
            failure?.PrNote,
            config.NeighbourFilter,
            options.GetInt("attempts", 3),
            options.GetInt("verify-runs", 3)));

        ReportWriter.Print(result, Console.WriteLine);
        WriteOutputs(result, Path.GetFullPath(options.Get("out") ?? Path.Combine(repo, "fixharness-out")));

        return result.Outcome switch
        {
            FixOutcome.Fixed => 0,
            FixOutcome.NotFixed => 1,
            _ => 2,
        };
    }

    public static PlantedFailure? FindFailure(CliOptions options, string repo) =>
        options.Get("failure") is { } id
            ? PlantedFailure.LoadAll(repo).FirstOrDefault(f => f.Id == id) ?? throw new ArgumentException($"Unknown failure id: {id}")
            : null;

    private static void WriteOutputs(FixResult result, string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "report.md"), ReportWriter.ToMarkdown(result));
        File.WriteAllText(Path.Combine(directory, "trace.json"), ReportWriter.ToJson(result));

        var patchPath = Path.Combine(directory, "fix.patch");
        if (result.Patch is { Length: > 0 }) File.WriteAllText(patchPath, result.Patch);
        else if (File.Exists(patchPath)) File.Delete(patchPath); // never leave a stale patch next to a failed run

        Console.WriteLine($"\nOutput written to {directory}");
    }
}
