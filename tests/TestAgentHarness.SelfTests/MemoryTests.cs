using TestAgentHarness.Harness;
using TestAgentHarness.Memory;
using TestAgentHarness.Model;
using TestAgentHarness.Testing;
using TestAgentHarness.Triage;

namespace TestAgentHarness.SelfTests;

/// <summary>Tests for the learning loop: signatures, replay, lessons, rejections and human labels.</summary>
public static class MemoryTests
{
    private const string Locator01 = "01-renamed-locator";
    private const string DiscountTest = "FullyQualifiedName~BasketFeature.DiscountCodeReducesTheTotal";

    public static void Register(Suite suite)
    {
        suite.Add("signature: ignores timings and line numbers, keeps what identifies the failure", () =>
        {
            var a = Signature("Timeout 5000ms exceeded.\nCall log:\n  - waiting for Locator(\"[data-testid='basket-total']\")\n at Steps.cs:line 48");
            var b = Signature("Timeout 30000ms exceeded.\nCall log:\n  - waiting for Locator(\"[data-testid='basket-total']\")\n at Steps.cs:line 61");
            var otherLocator = Signature("Timeout 5000ms exceeded.\nCall log:\n  - waiting for Locator(\"[data-testid='checkout']\")\n at Steps.cs:line 70");
            var unrelated = Signature("  Expected: \"£2.70\"\n  But was:  \"£2.90\"");

            Check.Equal(a.ExactKey, b.ExactKey, "same failure, different timing and line");
            Check.True(a.ExactKey != otherLocator.ExactKey, "a different locator is a different failure");
            Check.Equal(a.Shape, otherLocator.Shape, "but it is the same kind of failure");
            Check.True(FailureSignature.Similarity(a.Shape, unrelated.Shape) < 0.3, "an assertion mismatch is not similar to a locator timeout");
            Check.True(a.ExactKey != FailureSignature.From("another test", Failed("Timeout 5000ms exceeded.\nCall log:\n  - waiting for Locator(\"[data-testid='basket-total']\")")).ExactKey,
                "the same error in a different test is a different exact failure");
        });

        suite.Add("memory: a verified fix is replayed next time with no model call", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var memoryPath = Path.Combine(repo.Path, ".testagentharness", "memory.jsonl");

            var first = await Harness(HarnessTests.GoodLocatorFix(), memoryPath).RunAsync(TriageTests.Request(repo, Locator01));
            Check.Equal(FixOutcome.Fixed, first.Outcome, "first run outcome");
            Check.True(!first.ReplayedFromMemory, "first run used the model");
            Check.Equal(1, new FixMemory(memoryPath).Entries.Count(e => e.Kind == MemoryKind.Fix), "one fix remembered");

            var simulator = new TinyShopSimulator();
            var second = await Harness(new HarnessTests.ThrowingModel(), memoryPath, simulator).RunAsync(TriageTests.Request(repo, Locator01));
            Check.Equal(FixOutcome.Fixed, second.Outcome, "second run outcome");
            Check.True(second.ReplayedFromMemory, "second run replayed the fix");
            Check.Equal(0, second.ModelCalls, "model calls on the second run");
            Check.Equal(first.Patch, second.Patch, "same patch");
            Check.Equal(6, simulator.Runs, "the replay was fully re-verified (baseline, neighbours before, 3 target runs, neighbours after)");
            Check.Equal(1, new FixMemory(memoryPath).Entries.Single(e => e.Kind == MemoryKind.Fix).Uses, "use recorded");
            await HarnessTests.AssertRepoUntouched(repo);
        });

        suite.Add("memory: a remembered fix that stops working falls back to the model and is demoted", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var memoryPath = Path.Combine(repo.Path, ".testagentharness", "memory.jsonl");
            var request = TriageTests.Request(repo, Locator01);

            // Remember a "fix" that fixes nothing: it applies cleanly but the test still fails.
            var signature = (await Harness(HarnessTests.Script(HarnessTests.Say("giving up")), null).RunAsync(request with { MaxAttempts = 1 })).Signature!;
            const string uselessPatch = "diff --git a/testagentharness.json b/testagentharness.json\n--- a/testagentharness.json\n+++ b/testagentharness.json\n@@ -1,3 +1,3 @@\n {\n-  \"testProject\": \"sample/TinyShop.Tests\",\n+  \"testProject\":  \"sample/TinyShop.Tests\",\n   \"testRoots\": [\"sample/TinyShop.Tests\"],\n";
            new FixMemory(memoryPath).RecordFix(signature, request.Filter, "stale", uselessPatch, ["testagentharness.json"]);

            var result = await Harness(HarnessTests.GoodLocatorFix(), memoryPath).RunAsync(request);
            Check.Equal(FixOutcome.Fixed, result.Outcome, "outcome");
            Check.True(!result.ReplayedFromMemory, "the stale fix was not accepted");
            Check.Contains("order-total", result.Patch!, "the model's fix was used");
            Check.True(!result.Patch!.Contains("testagentharness.json"), "nothing from the failed replay leaked into the patch");

            var memory = new FixMemory(memoryPath);
            var stale = memory.Entries.Single(e => e.Lesson == "stale");
            Check.Equal(1, stale.Failures, "failure recorded against the stale fix");

            memory.RecordReplay(stale, succeeded: false);
            var replay = new FixMemory(memoryPath).FindReplay(signature);
            Check.True(replay is not null && replay.Lesson != "stale", "after two failures the stale fix is no longer replayed; the newer verified fix is");
        });

        suite.Add("memory: a fix for one test becomes a hint for a similar failure in another test", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var memoryPath = Path.Combine(repo.Path, ".testagentharness", "memory.jsonl");
            await Harness(HarnessTests.GoodLocatorFix(), memoryPath).RunAsync(TriageTests.Request(repo, Locator01));

            // Same planted failure, different test: not the exact failure, so no replay, but similar.
            var model = new RecordingModel(HarnessTests.GoodLocatorFix());
            var result = await Harness(model, memoryPath).RunAsync(TriageTests.Request(repo, Locator01) with { Filter = DiscountTest });

            Check.Equal(FixOutcome.Fixed, result.Outcome, "outcome");
            Check.True(!result.ReplayedFromMemory, "a different test is not replayed");
            Check.Equal(1, result.LessonsUsed.Count, "lessons used");
            Check.Contains("Lessons from earlier, similar failures", model.FirstTask!, "the lesson is in the model's context");
            Check.Contains("ShopPage.cs", model.FirstTask!, "the lesson names the file that was changed");
        });

        suite.Add("memory: rejected attempts are remembered as things not to repeat", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var memoryPath = Path.Combine(repo.Path, ".testagentharness", "memory.jsonl");
            var wrongFix = HarnessTests.Script(
                HarnessTests.Edit("sample/TinyShop.Tests/Pages/ShopPage.cs", "[data-testid='basket-total']", "[data-testid='basket-count']"),
                HarnessTests.Say("Fixed."));

            var failed = await Harness(wrongFix, memoryPath).RunAsync(TriageTests.Request(repo, Locator01) with { MaxAttempts = 1 });
            Check.Equal(FixOutcome.NotFixed, failed.Outcome, "outcome");
            Check.Equal(1, new FixMemory(memoryPath).Entries.Count(e => e.Kind == MemoryKind.Rejection), "rejection remembered");
            Check.Equal(0, new FixMemory(memoryPath).Entries.Count(e => e.Kind == MemoryKind.Fix), "an unverified fix is never remembered as a fix");

            var model = new RecordingModel(HarnessTests.GoodLocatorFix());
            await Harness(model, memoryPath).RunAsync(TriageTests.Request(repo, Locator01));
            Check.Contains("this attempt was rejected", model.FirstTask!, "the next run is warned");
        });

        suite.Add("memory: a person's label overrules the model from then on, and a model's opinion is never stored", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var memoryPath = Path.Combine(repo.Path, ".testagentharness", "memory.jsonl");
            var request = TriageTests.Request(repo, "03-async-total");
            ScriptedModelClient.Turn SaysDefect() => HarnessTests.Say("The total shows £0.00. The product is broken.\nVERDICT: PRODUCT_DEFECT");

            // Run 1: no rule matches, the model wrongly calls a test issue a product defect.
            var wrong = await Harness(HarnessTests.Script(SaysDefect()), memoryPath).RunAsync(request);
            Check.Equal(FixOutcome.ProductDefect, wrong.Outcome, "the misclassification happens");
            Check.Equal("model", wrong.Triage.Source, "it came from the model");
            Check.Equal(0, new FixMemory(memoryPath).Entries.Count, "nothing was learned from the model's opinion");

            // A person corrects it.
            new FixMemory(memoryPath).RecordLabel(wrong.Signature!, request.Filter, FailureKind.TestIssue, "The total is async now; the test reads too early.");

            // Run 2: the model says the same thing, is overruled, and the fix goes ahead.
            var fixScript = ScriptedModelClient.FromFile(Path.Combine(repo.Path, "failures", "scripts", "03-async-total.json"));
            var model = new SequenceModel(HarnessTests.Script(SaysDefect()), fixScript);
            var corrected = await Harness(model, memoryPath).RunAsync(request);

            Check.Equal("human label", corrected.Triage.Source, "triage source");
            Check.Equal(FixOutcome.Fixed, corrected.Outcome, "outcome");
            Check.Equal(2, corrected.Attempts.Count, "the model was overruled once, then fixed it");
        });

        suite.Add("memory: a person can also label a failure as a product defect", async () =>
        {
            using var repo = await TempRepo.CreateAsync();
            var memoryPath = Path.Combine(repo.Path, ".testagentharness", "memory.jsonl");
            var request = TriageTests.Request(repo, Locator01);

            var first = await Harness(HarnessTests.GoodLocatorFix(), null).RunAsync(request);
            new FixMemory(memoryPath).RecordLabel(first.Signature!, request.Filter, FailureKind.ProductDefect, "The rename was a mistake; revert it.");

            var result = await Harness(new HarnessTests.ThrowingModel(), memoryPath).RunAsync(request);
            Check.Equal(FixOutcome.ProductDefect, result.Outcome, "outcome");
            Check.Equal("human label", result.Triage.Source, "source");
            Check.Contains("revert it", ReportWriter.ToMarkdown(result), "the person's note is in the report");
        });

        suite.Add("memory: the file is plain JSON lines and survives a damaged line", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "tah-selftest-" + Guid.NewGuid().ToString("N")[..10] + ".jsonl");
            var signature = Signature("boom");
            new FixMemory(path).RecordRejection(signature, "t", "tried a sleep");
            File.AppendAllText(path, "this line is not json\n");
            new FixMemory(path).RecordRejection(signature, "t", "tried deleting the assertion");

            var memory = new FixMemory(path);
            Check.Equal(2, memory.Entries.Count, "entries");
            Check.Equal(2, memory.Lessons(signature).Count, "lessons for the same failure");
            new FixMemory(path).RecordRejection(signature, "t", "tried a sleep");
            Check.Equal(2, new FixMemory(path).Entries.Count, "the same rejection is not stored twice");
            File.Delete(path);
        });
    }

    private static FixRunner Harness(IModelClient model, string? memoryPath, TinyShopSimulator? simulator = null) =>
        new(model, simulator ?? new TinyShopSimulator()) { Memory = memoryPath is null ? null : new FixMemory(memoryPath) };

    private static TestRunResult Failed(string error) => new(true, [new TestCaseResult("t", TestOutcome.Failed, error)], "");

    private static FailureSignature Signature(string error) => FailureSignature.From("test", Failed(error));

    /// <summary>Wraps a model and keeps the first task it was given, to check what the harness put in the context.</summary>
    private sealed class RecordingModel(IModelClient inner) : IModelClient
    {
        public string? FirstTask { get; private set; }
        public string Name => inner.Name;

        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default)
        {
            FirstTask ??= messages[1].Content;
            return inner.CompleteAsync(messages, tools, cancellationToken);
        }
    }

    /// <summary>Uses one model per attempt, in order.</summary>
    private sealed class SequenceModel(params IModelClient[] models) : IModelClient
    {
        private int _index = -1;
        public string Name => "sequence";

        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default)
        {
            if (!messages.Any(m => m.Role == "assistant")) _index++;
            return models[Math.Min(_index, models.Length - 1)].CompleteAsync(messages, tools, cancellationToken);
        }
    }
}
