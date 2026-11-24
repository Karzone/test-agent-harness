using TestAgentHarness.Harness;
using TestAgentHarness.Model;
using TestAgentHarness.Testing;

namespace TestAgentHarness.SelfTests;

/// <summary>
/// End-to-end tests of the harness: a real git repository, real worktrees and
/// real patches, with a scripted model and a simulated test run.
/// </summary>
public static class HarnessTests
{
    public const string TotalTest = "FullyQualifiedName~BasketFeature.BasketTotalAddsUp";
    public const string AllTests = "FullyQualifiedName~TinyShop.Tests";
    public static readonly string[] TestRoots = ["sample/TinyShop.Tests"];

    private const string PageObject = "sample/TinyShop.Tests/Pages/ShopPage.cs";

    public static void Register(Suite suite)
    {
        suite.Add("harness: a cheating attempt is rejected and a real fix is verified (failure 01)", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var model = ScriptedModelClient.FromFile(Path.Combine(repo.Path, "failures", "scripts", "01-renamed-locator.json"));
            var harness = new FixRunner(model, new TinyShopSimulator());

            var result = await harness.RunAsync(new FixRequest(
                repo.Path, TotalTest, TestRoots, "failures/01-renamed-locator.patch", NeighbourFilter: AllTests));

            Check.Equal(FixOutcome.Fixed, result.Outcome, "outcome");
            Check.Equal(2, result.Attempts.Count, "attempts");

            var first = result.Attempts[0];
            Check.Equal(3, first.Loop.ToolEvents.Count(e => e.Result.StartsWith("Rejected by guardrail")), "guardrail rejections in attempt 1");
            Check.Contains("Fixed", first.Loop.Answer, "the model claimed success in attempt 1");
            Check.True(!first.Verification.Passed, "the verifier rejected attempt 1 anyway");
            Check.Contains("still fails", first.Verification.Reason, "why attempt 1 was rejected");

            Check.True(result.Attempts[1].Verification.Passed, "attempt 2 verified");
            Check.Contains("passed 3 of 3 runs", result.Summary, "summary mentions repeated runs");
            Check.Contains("+++ b/" + PageObject, result.Patch!, "patch touches the page object");
            Check.True(!result.Patch!.Contains("TinyShop.Web"), "patch does not touch product code");

            await AssertRepoUntouched(repo);

            // The verified patch must apply cleanly on top of the failing state.
            var patchFile = Path.Combine(Path.GetTempPath(), "tah-selftest-" + Guid.NewGuid().ToString("N")[..8] + ".patch");
            await File.WriteAllTextAsync(patchFile, result.Patch);
            await repo.GitAsync("apply", "failures/01-renamed-locator.patch");
            await repo.GitAsync("apply", "--check", patchFile);
            File.Delete(patchFile);
        });

        suite.Add("harness: a passing test is reported as not reproduced, without calling the model", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var result = await new FixRunner(new ThrowingModel(), new TinyShopSimulator())
                .RunAsync(new FixRequest(repo.Path, TotalTest, TestRoots));
            Check.Equal(FixOutcome.NotReproduced, result.Outcome, "outcome");
            Check.Equal(0, result.Attempts.Count, "attempts");
            await AssertRepoUntouched(repo);
        });

        suite.Add("harness: a filter that matches nothing is an error, not a pass", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var result = await new FixRunner(new ThrowingModel(), new TinyShopSimulator())
                .RunAsync(new FixRequest(repo.Path, "FullyQualifiedName~NoSuchTest", TestRoots));
            Check.Equal(FixOutcome.NoTestsMatched, result.Outcome, "outcome");
        });

        suite.Add("harness: a restore failure is an environment problem, a compile error is something to fix", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var restoreFailure = new FixedResultRunner(new TestRunResult(false, [], "error NU1301: Unable to load the service index for source https://api.nuget.org/v3/index.json."));
            var result = await new FixRunner(new ThrowingModel(), restoreFailure).RunAsync(new FixRequest(repo.Path, TotalTest, TestRoots));
            Check.Equal(FixOutcome.CouldNotRun, result.Outcome, "restore failure");

            Check.True(!new TestRunResult(false, [], "ShopSteps.cs(12,9): error CS0103: The name 'x' does not exist").IsEnvironmentFailure, "a compile error is not an environment failure");
        });

        suite.Add("harness: a fix that passes once and then fails is rejected as flaky", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            // Runs: 1 baseline, 2 the model's run_test, 3 verify run 1, 4 verify run 2.
            var simulator = new TinyShopSimulator { FailOnRun = 4 };
            var result = await new FixRunner(GoodLocatorFix(), simulator)
                .RunAsync(new FixRequest(repo.Path, TotalTest, TestRoots, "failures/01-renamed-locator.patch", MaxAttempts: 1));

            Check.Equal(FixOutcome.NotFixed, result.Outcome, "outcome");
            Check.Contains("flaky", result.Attempts[0].Verification.Reason, "reason");
            Check.True(result.Patch is null, "no patch is offered for an unverified fix");
        });

        suite.Add("harness: a fix that breaks a neighbouring test is rejected", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var model = Script(
                Edit(PageObject, "[data-testid='basket-total']", "[data-testid='order-total']"),
                Edit(PageObject, "[data-testid='basket-count']", "[data-testid='no-such-thing']"),
                Say("Fixed."));
            var result = await new FixRunner(model, new TinyShopSimulator())
                .RunAsync(new FixRequest(repo.Path, TotalTest, TestRoots, "failures/01-renamed-locator.patch", NeighbourFilter: AllTests, MaxAttempts: 1));

            Check.Equal(FixOutcome.NotFixed, result.Outcome, "outcome");
            Check.Contains("broke tests that were passing", result.Attempts[0].Verification.Reason, "reason");
            Check.Contains("AddingAnItemShowsItInTheBasket", result.Attempts[0].Verification.Reason, "names the broken test");
        });

        suite.Add("harness: the workspace is removed even when the run crashes", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var crashed = false;
            try
            {
                await new FixRunner(new ThrowingModel(), new TinyShopSimulator())
                    .RunAsync(new FixRequest(repo.Path, TotalTest, TestRoots, "failures/01-renamed-locator.patch"));
            }
            catch (InvalidOperationException)
            {
                crashed = true;
            }
            Check.True(crashed, "the model failure surfaced");
            await AssertRepoUntouched(repo);
        });
    }

    public static async Task AssertRepoUntouched(TempRepo repo)
    {
        Check.Equal("", (await repo.GitAsync("status", "--porcelain", "--untracked-files=no")).Trim(), "tracked files in the user's checkout are unchanged");
        var worktrees = (await repo.GitAsync("worktree", "list")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Check.Equal(1, worktrees.Length, "no worktree left behind");
        Check.Contains("basket-total", repo.Read(PageObject), "the user's page object is unchanged");
        Check.Equal("main", (await repo.GitAsync("rev-parse", "--abbrev-ref", "HEAD")).Trim(), "the user's branch is unchanged");
    }

    public static ScriptedModelClient GoodLocatorFix() => Script(
        Edit(PageObject, "[data-testid='basket-total']", "[data-testid='order-total']"),
        new ScriptedModelClient.Turn(null, [("run_test", "{}")]),
        Say("Updated the locator."));

    public static ScriptedModelClient Script(params ScriptedModelClient.Turn[] turns) => new([turns]);

    public static ScriptedModelClient.Turn Say(string text) => new(text, []);

    public static ScriptedModelClient.Turn Edit(string path, string oldText, string newText) =>
        new(null, [("replace_in_file", System.Text.Json.JsonSerializer.Serialize(new { path, old_text = oldText, new_text = newText }))]);

    /// <summary>A test runner that always returns the same result.</summary>
    public sealed class FixedResultRunner(TestRunResult result) : ITestRunner
    {
        public Task<TestRunResult> RunAsync(string workingDirectory, string filter, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    /// <summary>A model that must never be called; calling it fails the test.</summary>
    public sealed class ThrowingModel : IModelClient
    {
        public string Name => "throwing";

        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The model was called.");
    }
}
