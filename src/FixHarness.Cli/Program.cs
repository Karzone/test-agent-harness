using FixHarness.Cli;
using FixHarness.Context;
using FixHarness.Guardrails;
using FixHarness.Loop;
using FixHarness.Testing;
using FixHarness.Tools;

// Step 3: the loop now has guardrails. It cannot run forever, and it cannot
// edit product code, delete assertions, skip tests or add sleeps.
// Still missing: isolation (it edits your checkout) and verification.

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    Console.WriteLine("""
        fixharness list [--repo <path>]
        fixharness run  [--repo <path>] --failure <id> [--model <name>]
        fixharness run  [--repo <path>] --filter <dotnet test filter> [--model <name>]

        Model: set FIXHARNESS_API_KEY, FIXHARNESS_MODEL and optionally FIXHARNESS_BASE_URL
        (any OpenAI-compatible endpoint), or pass --model scripted:<script.json>.
        """);
    return 0;
}

var options = new CliOptions(args.Skip(1));
var repo = Path.GetFullPath(options.Get("repo") ?? Directory.GetCurrentDirectory());
var failures = PlantedFailure.LoadAll(repo);

if (args[0] == "list")
{
    foreach (var item in failures) Console.WriteLine($"{item.Id,-22} {item.Kind,-15} {item.Title}");
    return 0;
}

if (args[0] != "run")
{
    Console.Error.WriteLine($"Unknown command: {args[0]}");
    return 2;
}

var config = RepoConfig.Load(repo);
var failure = options.Get("failure") is { } id
    ? failures.FirstOrDefault(f => f.Id == id) ?? throw new ArgumentException($"Unknown failure id: {id}")
    : null;
var filter = options.Get("filter") ?? failure?.Filter ?? throw new ArgumentException("Pass --failure or --filter.");

if (failure is not null)
{
    // The bare loop has nowhere safe to work, so the failure is planted straight into this checkout.
    var plant = await ProcessRunner.RunAsync("git", ["apply", failure.Patch], repo, TimeSpan.FromSeconds(30));
    if (!plant.Succeeded) throw new InvalidOperationException("Could not plant the failure: " + plant.Output);
    Console.WriteLine($"Planted {failure.Id} into {repo}. Undo with: git checkout -- .\n");
}

var runner = new DotnetTestRunner(config.TestProject);
var model = ModelFactory.Create(options.Get("model"), repo);

Console.WriteLine($"Model: {model.Name}\nTest:  {filter}\n\nRunning the test once to capture the failure...");
var baseline = await runner.RunAsync(repo, filter);
Console.WriteLine(baseline.Describe() + "\n");

var policy = new PatchPolicy(new PatchPolicyOptions(config.TestRoots));
var tools = WorkspaceTools.Create(repo, runner, filter, policy);
var messages = ContextBuilder.Create(filter, baseline.Describe(), failure?.PrNote);
var result = await AgentLoop.RunAsync(model, messages, tools, LoopGuardrails.Default, Console.WriteLine);

Console.WriteLine($"\nAnswer: {result.Answer}");
Console.WriteLine($"Stopped by: {result.StoppedBy} after {result.Iterations} iteration(s)");
Console.WriteLine("\nNothing has checked that answer. Look at 'git diff' to see what was actually changed.");
return 0;
