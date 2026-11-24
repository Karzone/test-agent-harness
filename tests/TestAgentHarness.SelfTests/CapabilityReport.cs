using System.Text;
using System.Text.Json;
using TestAgentHarness.Eval;
using TestAgentHarness.Harness;
using TestAgentHarness.Memory;
using TestAgentHarness.Model;
using TestAgentHarness.Story;
using TestAgentHarness.Triage;

namespace TestAgentHarness.SelfTests;

/// <summary>One capability, tested end to end once.</summary>
public sealed record CapabilityCase(
    string Id,
    string Area,
    string Capability,
    string HowTested,
    string Environment,
    bool Passed,
    string Result,
    string Evidence,
    double Seconds);

/// <summary>
/// Runs one end-to-end test per capability and writes the outcome as markdown
/// and JSON, with the evidence each run produced (rejections, patches, triage
/// verdicts, screenshots). This is a demonstration with known inputs, not a
/// measure of how a real model performs.
/// </summary>
public static class CapabilityReport
{
    private const string Simulated = "Real git worktrees and patches. Scripted model. Simulated test run.";
    private const string RealBrowser = "Real Chromium via Playwright. Scripted model.";
    private const string Locator01 = "01-renamed-locator";

    public static async Task<int> RunAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var cases = new List<CapabilityCase>();

        async Task Run(string id, string area, string capability, string how, string environment, Func<Task<(string Result, string Evidence)>> body)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var (result, evidence) = await body();
                cases.Add(new CapabilityCase(id, area, capability, how, environment, true, result, evidence, clock.Elapsed.TotalSeconds));
                Console.WriteLine($"  PASS  {id} {capability}");
            }
            catch (Exception error)
            {
                cases.Add(new CapabilityCase(id, area, capability, how, environment, false, "FAILED: " + error.Message, "", clock.Elapsed.TotalSeconds));
                Console.WriteLine($"  FAIL  {id} {capability}\n        {error.Message}");
            }
        }

        // ── Auto-fix ─────────────────────────────────────────────────────────
        await Run("F1", "Auto-fix", "Fix a broken test and prove the fix",
            "Planted failure 01 (a renamed test id). The scripted model first tries to cheat, then makes the real fix.", Simulated, async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var model = ScriptedModelClient.FromFile(Path.Combine(repo.Path, "failures", "scripts", Locator01 + ".json"));
            var result = await new FixRunner(model, new TinyShopSimulator()).RunAsync(TriageTests.Request(repo, Locator01));

            Check.Equal(FixOutcome.Fixed, result.Outcome, "outcome");
            Check.Equal(2, result.Attempts.Count, "attempts");
            await HarnessTests.AssertRepoUntouched(repo);

            var rejected = result.Attempts[0].Loop.ToolEvents.Where(e => e.Result.StartsWith("Rejected by guardrail")).Select(e => "- " + FirstViolation(e.Result));
            return ($"Fixed on attempt 2 of 2. Attempt 1 was thrown away. The user's checkout was not touched.",
                "Attempt 1, edits the guardrails refused:\n" + string.Join('\n', rejected)
                + $"\n\nAttempt 1, the model then said: \"{result.Attempts[0].Loop.Answer}\"\nVerifier: {FirstLine(result.Attempts[0].Verification.Reason)}"
                + $"\n\nAttempt 2, verifier: {result.Attempts[1].Verification.Reason}"
                + "\n\nVerified patch:\n```diff\n" + PatchBody(result.Patch!) + "\n```");
        });

        await Run("F2", "Auto-fix", "Turn a rejected shortcut into a proper fix",
            "Planted failure 03 (the total now arrives late). The scripted model reaches for a sleep, is refused, and switches to a retrying assertion.", Simulated, async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var model = ScriptedModelClient.FromFile(Path.Combine(repo.Path, "failures", "scripts", "03-async-total.json"));
            var result = await new FixRunner(model, new TinyShopSimulator()).RunAsync(TriageTests.Request(repo, "03-async-total"));

            Check.Equal(FixOutcome.Fixed, result.Outcome, "outcome");
            Check.Equal(1, result.Attempts.Count, "attempts");
            var rejection = result.Attempts[0].Loop.ToolEvents.Single(e => e.Result.StartsWith("Rejected by guardrail"));
            Check.Contains("fixed wait", rejection.Result, "the sleep was refused");
            return ("Fixed in one attempt. The sleep was refused inside the loop and the model corrected course.",
                "Refused edit: " + FirstViolation(rejection.Result) + "\n\nVerified patch:\n```diff\n" + PatchBody(result.Patch!) + "\n```");
        });

        await Run("F3", "Auto-fix", "Reject a fix that only passes sometimes",
            "A correct-looking fix whose test passes on the first verification run and fails on the second.", Simulated, async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var simulator = new TinyShopSimulator { FailOnRun = 4 };
            var result = await new FixRunner(HarnessTests.GoodLocatorFix(), simulator)
                .RunAsync(new FixRequest(repo.Path, HarnessTests.TotalTest, HarnessTests.TestRoots, "failures/01-renamed-locator.patch", MaxAttempts: 1));

            Check.Equal(FixOutcome.NotFixed, result.Outcome, "outcome");
            Check.True(result.Patch is null, "no patch offered");
            return ("Not fixed, and no patch was offered.", "Verifier: " + FirstLine(result.Attempts[0].Verification.Reason));
        });

        // ── Triage ───────────────────────────────────────────────────────────
        await Run("T1", "Triage", "Report a product defect instead of 'fixing' the test",
            "Planted failure 05 (the checkout handler crashes). A model that fails the test if it is ever called.", Simulated, async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var result = await new FixRunner(new HarnessTests.ThrowingModel(), new TinyShopSimulator()).RunAsync(TriageTests.Request(repo, "05-checkout-crash"));

            Check.Equal(FixOutcome.ProductDefect, result.Outcome, "outcome");
            Check.Equal(0, result.ModelCalls, "model calls");
            return ($"Product defect, decided by the rule {result.Triage.Source} with 0 model calls. No patch produced.",
                "Triage reason: " + result.Triage.Reason);
        });

        await Run("T2", "Triage", "Overrule a model that blames the product for a test issue",
            "Planted failure 01. The scripted model first declares a product defect; the rules have evidence it is a stale locator.", Simulated, async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var model = new ScriptedModelClient(
            [
                [HarnessTests.Say("The total element is missing. The product is broken.\nVERDICT: PRODUCT_DEFECT")],
                [HarnessTests.Edit("sample/TinyShop.Tests/Pages/ShopPage.cs", "[data-testid='basket-total']", "[data-testid='order-total']"), HarnessTests.Say("Fixed the locator.\nVERDICT: TEST_ISSUE")],
            ]);
            var result = await new FixRunner(model, new TinyShopSimulator()).RunAsync(TriageTests.Request(repo, Locator01));

            Check.Equal(FixOutcome.Fixed, result.Outcome, "outcome");
            Check.Equal(2, result.Attempts.Count, "attempts");
            return ($"Fixed. The model's 'product defect' verdict was overruled by the rule {result.Triage.Source} and it was made to try again.",
                "What the harness told the model: " + result.Attempts[0].Verification.Reason);
        });

        // ── Learning loop ────────────────────────────────────────────────────
        await Run("L1", "Learning loop", "Replay a known fix without calling the model",
            "Planted failure 01 twice with the same memory file. The second run uses a model that fails the test if called.", Simulated, async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var memoryPath = Path.Combine(repo.Path, ".testagentharness", "memory.jsonl");
            var first = await new FixRunner(HarnessTests.GoodLocatorFix(), new TinyShopSimulator()) { Memory = new FixMemory(memoryPath) }
                .RunAsync(TriageTests.Request(repo, Locator01));
            var second = await new FixRunner(new HarnessTests.ThrowingModel(), new TinyShopSimulator()) { Memory = new FixMemory(memoryPath) }
                .RunAsync(TriageTests.Request(repo, Locator01));

            Check.Equal(FixOutcome.Fixed, second.Outcome, "second outcome");
            Check.True(second.ReplayedFromMemory, "replayed");
            Check.Equal(0, second.ModelCalls, "model calls on the second run");
            return ($"Run 1: fixed with {first.ModelCalls} model calls. Run 2: fixed with 0 model calls, by replaying the remembered patch and verifying it again.",
                "Run 2 summary: " + second.Summary);
        });

        await Run("L2", "Learning loop", "Make a person's correction stick",
            "Planted failure 03 with no matching rule. Run 1: the model wrongly reports a product defect. A person labels it a test issue. Run 2: the model says the same thing again.", Simulated, async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var memoryPath = Path.Combine(repo.Path, ".testagentharness", "memory.jsonl");
            var request = TriageTests.Request(repo, "03-async-total");
            ScriptedModelClient SaysDefect() => HarnessTests.Script(HarnessTests.Say("The total shows £0.00. The product is broken.\nVERDICT: PRODUCT_DEFECT"));

            var wrong = await new FixRunner(SaysDefect(), new TinyShopSimulator()) { Memory = new FixMemory(memoryPath) }.RunAsync(request);
            Check.Equal(FixOutcome.ProductDefect, wrong.Outcome, "run 1 misclassifies");
            Check.Equal(0, new FixMemory(memoryPath).Entries.Count, "nothing learned from the model's opinion");

            new FixMemory(memoryPath).RecordLabel(wrong.Signature!, request.Filter, FailureKind.TestIssue, "The total is async now; the test reads too early.");

            var fixScript = ScriptedModelClient.FromFile(Path.Combine(repo.Path, "failures", "scripts", "03-async-total.json"));
            var corrected = await new FixRunner(new TwoPhaseModel(SaysDefect(), fixScript), new TinyShopSimulator()) { Memory = new FixMemory(memoryPath) }.RunAsync(request);

            Check.Equal("human label", corrected.Triage.Source, "triage source");
            Check.Equal(FixOutcome.Fixed, corrected.Outcome, "run 2 outcome");
            return ("Run 1: reported as a product defect (wrong), and nothing was stored from the model's opinion. After the label, run 2: classified by 'human label', the model's repeat verdict was overruled, and the test was fixed.",
                $"Run 1 triage source: {wrong.Triage.Source}\nRun 2 triage source: {corrected.Triage.Source}\nRun 2 attempts: {corrected.Attempts.Count} (overruled once, then fixed)");
        });

        // ── Eval ─────────────────────────────────────────────────────────────
        await Run("E1", "Eval", "Score every planted failure against its known answer",
            "All five planted failures, each with its scripted model.", Simulated, async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            using var manifest = JsonDocument.Parse(repo.Read("failures/manifest.json"));
            var evalCases = manifest.RootElement.EnumerateArray().Select(entry =>
            {
                var id = entry.GetProperty("id").GetString()!;
                var expected = entry.GetProperty("kind").GetString() == "product-defect" ? FailureKind.ProductDefect : FailureKind.TestIssue;
                return new EvalCase(id, expected, TriageTests.Request(repo, id));
            }).ToList();

            var summary = await EvalRunner.RunAsync("scripted", evalCases, evalCase =>
            {
                var script = Path.Combine(repo.Path, "failures", "scripts", evalCase.Id + ".json");
                IModelClient model = File.Exists(script) ? ScriptedModelClient.FromFile(script) : new HarnessTests.ThrowingModel();
                return new FixRunner(model, new TinyShopSimulator());
            });

            Check.Equal(5, summary.Correct, "correct");
            Check.True(!summary.HasHarmfulResult, "no harmful result");
            var rows = summary.Cases.Select(c => $"| {c.Id} | {c.Expected} | {c.Outcome} | {c.Verdict} | {c.TriageSource} | {c.ModelCalls} |");
            return ($"{summary.Correct} of {summary.Cases.Count} correct. False fixes: {summary.FalseFixes}. Wrongly blocked: {summary.WronglyBlocked}. Decided without the model: {summary.DecidedWithoutModel}.",
                "| Failure | Expected | Outcome | Verdict | Decided by | Model calls |\n|---|---|---|---|---|---|\n" + string.Join('\n', rows));
        });

        // ── Story testing ────────────────────────────────────────────────────
        await Run("S1", "Story testing", "Check a story on a healthy app",
            "The discount-code story (3 acceptance criteria) against the unmodified sample shop.", RealBrowser, async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var result = await StoryTests.RunAsync(repo, "discount-code.json", Path.Combine(outputDirectory, "S1"));
            Check.Equal(StoryOutcome.Passed, result.Outcome, "outcome");
            return ("Passed: 3 of 3 criteria, each with evidence the harness read from the page and a screenshot.", CriteriaTable(result));
        });

        await Run("S2", "Story testing", "Find a product defect while checking a story",
            "The same story against the shop with planted defect 04 (SAVE10 takes 10p off instead of 10%).", RealBrowser, async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            await repo.GitAsync("apply", "failures/04-wrong-discount.patch");
            var result = await StoryTests.RunAsync(repo, "discount-code.json", Path.Combine(outputDirectory, "S2"));
            Check.Equal(StoryOutcome.Failed, result.Outcome, "outcome");
            Check.Equal("£2.90", result.Criteria.Single(c => c.Id == "AC2").Evidence, "AC2 evidence");
            var refused = result.Loop.ToolEvents.First(e => e.Result.StartsWith("Not recorded"));
            return ("Failed: AC2 shows £2.90 where the story expects £2.70. AC1 and AC3 passed.",
                "The tester first tried to record AC2 as a pass. The harness answered: " + refused.Result + "\n\n" + CriteriaTable(result));
        });

        await Run("S3", "Story testing", "Refuse a 'pass' that has no evidence",
            "The defective shop again, with a scripted tester that announces success without recording AC2.", RealBrowser, async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            await repo.GitAsync("apply", "failures/04-wrong-discount.patch");
            var result = await StoryTests.RunAsync(repo, "discount-code-overclaims.json", Path.Combine(outputDirectory, "S3"));
            Check.Equal(StoryOutcome.Incomplete, result.Outcome, "outcome");
            return ("Incomplete, not passed: AC2 is reported as unverified.",
                $"The tester's final answer: \"{result.Loop.Answer}\"\n\n" + CriteriaTable(result));
        });

        await Run("S4", "Story testing", "Keep the tester inside the story's limits",
            "A scripted tester that tries to open another website and to click the forbidden Checkout button before doing the job.", RealBrowser, async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var result = await StoryTests.RunAsync(repo, "discount-code-wanders.json", Path.Combine(outputDirectory, "S4"));
            var blocked = result.Loop.ToolEvents.Where(e => e.Result.StartsWith("Blocked by guardrail")).ToList();
            Check.Equal(2, blocked.Count, "blocked actions");
            Check.Equal(StoryOutcome.Passed, result.Outcome, "outcome");
            return ("Both off-limits actions were blocked before reaching the browser, and the story still passed 3 of 3.",
                string.Join('\n', blocked.Select(e => "- " + e.Result)));
        });

        File.WriteAllText(Path.Combine(outputDirectory, "capability-report.json"),
            JsonSerializer.Serialize(cases, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        File.WriteAllText(Path.Combine(outputDirectory, "capability-report.md"), ToMarkdown(cases));

        var failed = cases.Count(c => !c.Passed);
        Console.WriteLine(failed == 0 ? $"\n{cases.Count} of {cases.Count} capability tests passed." : $"\n{failed} capability test(s) FAILED.");
        Console.WriteLine($"Report written to {outputDirectory}");
        return failed == 0 ? 0 : 1;
    }

    private static string ToMarkdown(IReadOnlyList<CapabilityCase> cases)
    {
        var text = new StringBuilder();
        text.AppendLine("# test-agent-harness: capability test report");
        text.AppendLine();
        text.AppendLine($"{cases.Count(c => c.Passed)} of {cases.Count} capability tests passed.");
        text.AppendLine();
        text.AppendLine("Every model in these tests is scripted: a fixed sequence of tool calls, chosen to show how the harness reacts. The auto-fix, triage, learning and eval tests use real git worktrees and patches with a simulated test run. The story tests drive a real browser.");
        text.AppendLine();
        text.AppendLine("| # | Area | Capability | Result | Seconds |");
        text.AppendLine("|---|---|---|---|---|");
        foreach (var c in cases) text.AppendLine($"| {c.Id} | {c.Area} | {c.Capability} | {(c.Passed ? "Pass" : "FAIL")} | {c.Seconds:0.0} |");

        foreach (var c in cases)
        {
            text.AppendLine();
            text.AppendLine($"## {c.Id}. {c.Capability}");
            text.AppendLine();
            text.AppendLine($"- **Test:** {c.HowTested}");
            text.AppendLine($"- **Ran on:** {c.Environment}");
            text.AppendLine($"- **Result:** {c.Result}");
            if (c.Evidence.Length > 0)
            {
                text.AppendLine();
                text.AppendLine(c.Evidence);
            }
        }
        return text.ToString();
    }

    private static string CriteriaTable(StoryResult result) =>
        "| Criterion | Result | Evidence read from the page |\n|---|---|---|\n"
        + string.Join('\n', result.Criteria.Select(c => $"| {c.Id}: {c.Text} | {c.Status}{(c.NeedsReview ? " (review)" : "")} | {c.Evidence} |"));

    private static string FirstLine(string text) => text.Split('\n')[0];

    private static string FirstViolation(string rejection) =>
        rejection.Split('\n').Skip(1).FirstOrDefault()?.TrimStart('-', ' ') ?? rejection;

    /// <summary>The changed lines of a patch, without the git header noise.</summary>
    private static string PatchBody(string patch) =>
        string.Join('\n', patch.Split('\n').Where(l => l.StartsWith("+++ ") || l.StartsWith("@@") || ((l.StartsWith('+') || l.StartsWith('-')) && !l.StartsWith("---") && !l.StartsWith("+++")))).TrimEnd();

    /// <summary>Plays the first model for the first attempt and the second model after that.</summary>
    private sealed class TwoPhaseModel(IModelClient first, IModelClient later) : IModelClient
    {
        private int _attempt;
        public string Name => "scripted";

        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default)
        {
            if (!messages.Any(m => m.Role == "assistant")) _attempt++;
            return (_attempt <= 1 ? first : later).CompleteAsync(messages, tools, cancellationToken);
        }
    }
}
