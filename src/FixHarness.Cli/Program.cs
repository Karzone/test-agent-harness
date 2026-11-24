using FixHarness.Cli;
using FixHarness.Harness;
using FixHarness.Testing;

// Step 4: the harness owns the run. The agent works in a throwaway git worktree,
// so your checkout is never touched, and the result is a patch file.
// Still missing: nothing has checked that the patch works.

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    Console.WriteLine("""
        fixharness list [--repo <path>]
        fixharness run  [--repo <path>] --failure <id> [--model <name>] [--out <dir>]
        fixharness run  [--repo <path>] --filter <dotnet test filter> [--model <name>] [--out <dir>]

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

var model = ModelFactory.Create(options.Get("model"), repo);
var harness = new FixHarnessRunner(model, new DotnetTestRunner(config.TestProject), Console.WriteLine);

Console.WriteLine($"Model: {model.Name}\nTest:  {filter}\n");
var run = await harness.RunAsync(new FixRequest(repo, filter, config.TestRoots, failure?.Patch, failure?.PrNote));

if (run.Loop is null)
{
    Console.WriteLine(run.Baseline.NoTestsMatched
        ? "The filter matched no tests. Nothing was run."
        : "The test already passes. Nothing to fix.");
    return 1;
}

Console.WriteLine("\n--- Agent trace ---\n");
TracePrinter.Print(run.Loop, Console.WriteLine);
Console.WriteLine($"\n--- Agent's answer (unverified) ---\n\n{run.Loop.Answer}");

var outDirectory = Path.GetFullPath(options.Get("out") ?? Path.Combine(repo, "fixharness-out"));
Directory.CreateDirectory(outDirectory);
var patchPath = Path.Combine(outDirectory, "fix.patch");
File.WriteAllText(patchPath, run.Patch);
Console.WriteLine(run.Patch.Length == 0 ? "\nThe agent changed nothing." : $"\nPatch written to {patchPath}");
return 0;
