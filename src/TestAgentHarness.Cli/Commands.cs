using System.Text.Json;
using TestAgentHarness.Eval;
using TestAgentHarness.Harness;
using TestAgentHarness.Memory;
using TestAgentHarness.Story;
using TestAgentHarness.Testing;
using TestAgentHarness.Triage;

namespace TestAgentHarness.Cli;

public static class Commands
{
    public const string Help = """
        tah fix [--repo <path>] --failure <id>
        tah fix [--repo <path>] --filter <dotnet test filter> [--base <git ref>] [--note <text>]
            Triage one failing test, then fix it or report a product defect.
              --project <path>    test project to run, when the repository has more than one (default: testagentharness.json)
              --test-roots <a,b>  the only folders a fix may touch (default: testagentharness.json)
              --neighbours <f>    filter for neighbouring tests that must not break
              --base <ref>        the change that preceded the failure is the diff from <ref> to HEAD
              --note <text>       pull-request description, used as evidence
              --model <name>      model name, or scripted:<file-or-directory> to replay a script
              --attempts <n>      attempts before giving up (default 3)
              --verify-runs <n>   times the fixed test must pass (default 3)
              --no-memory         do not read or write .testagentharness/memory.jsonl
              --out <dir>         where to write report.md, trace.json and fix.patch

        tah story --story <story.json> [--repo <path>] [--model <name>] [--out <dir>]
            Check a user story in a real browser. Each acceptance criterion is passed or
            failed only on evidence the harness reads from the page itself.
            Needs Node.js, and "npm install" run once in tools/browser-sidecar.

        tah feedback --label test-issue|product-defect|environment [--note <text>] [--out <dir>]
            Correct the classification of the last run. The same failure is then
            always classified your way, whatever the rules or the model say.

        tah memory [--repo <path>]
            Show what the harness has learned in this repository.

        tah eval [--repo <path>] [--model <name>] [--with-memory]
            Run every planted failure and score the results against the known answers.

        tah list [--repo <path>]
            List the planted failures in failures/manifest.json.

        Model: set TAH_API_KEY, TAH_MODEL and optionally TAH_BASE_URL
        (any OpenAI-compatible endpoint: OpenAI, Azure OpenAI, OpenRouter).

        Exit codes for fix: 0 fixed, 1 not fixed, 3 product defect, 4 environment problem,
                            2 could not run, nothing to do, or bad usage.
        Exit codes for story: 0 all criteria passed, 1 a criterion failed, 2 a criterion could not be verified.
        Exit codes for eval: 0 no harmful result, 1 a false fix or a wrongly blocked test issue.
        """;

    public static int List(string repo)
    {
        foreach (var item in PlantedFailure.LoadAll(repo))
        {
            Console.WriteLine($"{item.Id,-26} {(item.TestProject is null ? "ui" : "api"),-4} {item.Kind,-15} {item.Title}");
        }
        return 0;
    }

    public static async Task<int> RunAsync(CliOptions options, string repo)
    {
        var failure = FindFailure(options, repo);
        var config = RepoConfig.Load(repo).For(options, failure);
        var filter = options.Get("filter") ?? failure?.Filter ?? throw new ArgumentException("Pass --failure or --filter.");

        var model = ModelFactory.Create(options.Get("model"), repo, failure?.Id);
        var harness = new FixRunner(model, new DotnetTestRunner(config.TestProject), Console.WriteLine)
        {
            Memory = options.Get("no-memory") is null ? new FixMemory(MemoryPath(repo)) : null,
        };

        Console.WriteLine($"Model: {model.Name}\nTest:  {filter}\n");
        var result = await harness.RunAsync(await BuildRequestAsync(options, repo, config, failure, filter));

        ReportWriter.Print(result, Console.WriteLine);
        WriteOutputs(result, OutDirectory(options, repo));
        return result.Outcome switch
        {
            FixOutcome.Fixed => 0,
            FixOutcome.NotFixed => 1,
            FixOutcome.ProductDefect => 3,
            FixOutcome.EnvironmentIssue => 4,
            _ => 2,
        };
    }

    public static async Task<int> StoryAsync(CliOptions options, string repo)
    {
        var storyPath = Path.GetFullPath(Path.Combine(repo, options.Get("story") ?? throw new ArgumentException("Pass --story <story.json>.")));
        var story = UserStory.Load(storyPath, repo);
        var model = ModelFactory.Create(options.Get("model"), repo);
        var sidecar = Path.Combine(repo, "tools", "browser-sidecar", "sidecar.mjs");

        Console.WriteLine($"Model: {model.Name}\nStory: {story.Title}\n");
        var runner = new StoryRunner(model, async () => await SidecarBrowser.StartAsync(sidecar), Console.WriteLine);
        var directory = Path.Combine(OutDirectory(options, repo), "story");
        var result = await runner.RunAsync(story, directory);

        var markdown = StoryRunner.ToMarkdown(result);
        Console.WriteLine("\n" + markdown);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "story-report.md"), markdown);
        File.WriteAllText(Path.Combine(directory, "story-trace.json"), ReportWriter.ToJson(result));
        Console.WriteLine($"Output written to {directory}");

        return result.Outcome switch
        {
            StoryOutcome.Passed => 0,
            StoryOutcome.Failed => 1,
            _ => 2,
        };
    }

    public static int Feedback(CliOptions options, string repo)
    {
        var label = options.Get("label") switch
        {
            "test-issue" => FailureKind.TestIssue,
            "product-defect" => FailureKind.ProductDefect,
            "environment" => FailureKind.Environment,
            _ => throw new ArgumentException("Pass --label test-issue, --label product-defect or --label environment."),
        };

        var lastFailurePath = Path.Combine(OutDirectory(options, repo), "last-failure.json");
        if (!File.Exists(lastFailurePath)) throw new FileNotFoundException("No previous run found. Run 'tah fix' first, or pass the same --out directory.");

        using var document = JsonDocument.Parse(File.ReadAllText(lastFailurePath));
        var root = document.RootElement;
        var signature = new FailureSignature(
            root.GetProperty("ExactKey").GetString()!,
            root.GetProperty("Shape").GetString()!,
            root.GetProperty("Headline").GetString()!);

        new FixMemory(MemoryPath(repo)).RecordLabel(signature, root.GetProperty("Filter").GetString()!, label, options.Get("note"));
        Console.WriteLine($"Recorded: \"{signature.Headline}\" is a {options.Get("label")}. This exact failure will be classified that way from now on.");
        return 0;
    }

    public static int ShowMemory(string repo)
    {
        var memory = new FixMemory(MemoryPath(repo));
        if (memory.Entries.Count == 0)
        {
            Console.WriteLine("Nothing learned yet.");
            return 0;
        }

        foreach (var entry in memory.Entries)
        {
            var detail = entry.Kind switch
            {
                MemoryKind.Fix => $"changed {string.Join(", ", entry.Files ?? [])}; replayed {entry.Uses}x, failed {entry.Failures}x",
                MemoryKind.Label => $"labelled {entry.Label}",
                _ => entry.Lesson,
            };
            Console.WriteLine($"{entry.CreatedUtc:yyyy-MM-dd}  {entry.Kind,-9}  {entry.Headline}\n            {detail}");
        }
        return 0;
    }

    public static async Task<int> EvalAsync(CliOptions options, string repo)
    {
        var defaults = RepoConfig.Load(repo);
        var withMemory = options.Get("with-memory") is not null;

        // Each planted failure names its own test suite (UI or API), so each case gets its own runner.
        var cases = new List<EvalCase>();
        var runners = new Dictionary<string, DotnetTestRunner>();
        foreach (var failure in PlantedFailure.LoadAll(repo))
        {
            var config = defaults.For(options, failure);
            var expected = failure.Kind == "product-defect" ? FailureKind.ProductDefect : FailureKind.TestIssue;
            cases.Add(new EvalCase(failure.Id, expected, await BuildRequestAsync(options, repo, config, failure, failure.Filter)));
            runners[failure.Id] = new DotnetTestRunner(config.TestProject);
        }

        var modelName = options.Get("model") ?? Environment.GetEnvironmentVariable("TAH_MODEL") ?? "(unset)";
        var summary = await EvalRunner.RunAsync(
            modelName,
            cases,
            // A fresh model client per case; memory is off by default so runs are comparable.
            evalCase => new FixRunner(ModelFactory.Create(options.Get("model"), repo, evalCase.Id), runners[evalCase.Id], Console.WriteLine)
            {
                Memory = withMemory ? new FixMemory(MemoryPath(repo)) : null,
            },
            Console.WriteLine);

        var markdown = EvalRunner.ToMarkdown(summary);
        Console.WriteLine("\n" + markdown);

        var directory = OutDirectory(options, repo);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "eval.md"), markdown);
        File.WriteAllText(Path.Combine(directory, "eval.json"), ReportWriter.ToJson(summary));
        return summary.HasHarmfulResult ? 1 : 0;
    }

    private static async Task<FixRequest> BuildRequestAsync(CliOptions options, string repo, RepoConfig config, PlantedFailure? failure, string filter)
    {
        // What changed before the test broke: the planted patch, or the diff from --base to HEAD.
        string? changeDiff = null;
        if (failure is not null) changeDiff = await File.ReadAllTextAsync(Path.Combine(repo, failure.Patch));
        else if (options.Get("base") is { } baseRef) changeDiff = await GitChanges.DiffSinceAsync(repo, baseRef);

        return new FixRequest(
            repo,
            filter,
            config.TestRoots,
            failure?.Patch,
            options.Get("note") ?? failure?.PrNote,
            config.NeighbourFilter,
            options.GetInt("attempts", 3),
            options.GetInt("verify-runs", 3),
            changeDiff);
    }

    private static string MemoryPath(string repo) => Path.Combine(repo, ".testagentharness", "memory.jsonl");

    private static string OutDirectory(CliOptions options, string repo) =>
        Path.GetFullPath(options.Get("out") ?? Path.Combine(repo, "tah-out"));

    private static PlantedFailure? FindFailure(CliOptions options, string repo) =>
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

        // Lets "tah feedback" label this failure afterwards.
        if (result.Signature is { } signature)
        {
            File.WriteAllText(
                Path.Combine(directory, "last-failure.json"),
                ReportWriter.ToJson(new { result.Filter, signature.ExactKey, signature.Shape, signature.Headline }));
        }

        Console.WriteLine($"\nOutput written to {directory}");
    }
}
